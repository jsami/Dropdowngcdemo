using System.Diagnostics;
using System.Text;
using HtmlAgilityPack;

// ----------------------------------------------------------------------------
// Reproduction of a real-world GC pressure issue from a legacy MVC5 dossier
// search form. A dropdown with 1000+ items was being constructed by string
// concatenation + string.Replace in a loop, allocating short-lived strings
// like crazy. Under concurrent load (many users hitting the page during work
// hours), Gen 2 collections piled up and the page became slow.
//
// This console app runs both implementations N times to expose the difference
// in allocations / GC counts. Run it under `dotnet-counters` or `dotnet-trace`
// for full effect.
// ----------------------------------------------------------------------------

const int OptionCount = 1500;       // dossiers in the dropdown
const int Iterations = 1000;         // simulate 500-page renders
const int ConcurrentUsers = 16;     // simulate concurrent backoffice users

// Build a fake dataset that looks like the real one:
// each dossier has an Id, a Label, and a "Kind" that drives a custom color.
var kinds = new[] { "Active", "Pending", "Closed", "Urgent", "Archived" };
var dossiers = Enumerable.Range(0, OptionCount)
    .Select(i => new Dossier(i, $"Dossier #{i:0000} - some moderately long label", kinds[i % kinds.Length]))
    .ToArray();

Console.WriteLine($".NET {Environment.Version}  |  ServerGC={System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine($"Options per render: {OptionCount}  |  Iterations: {Iterations}  |  Concurrent: {ConcurrentUsers}");
Console.WriteLine(new string('-', 70));

Run("SLOW: string concat + Replace in loop", () => BuildDropdownSlow(dossiers));
Run("FAST  : StringBuilder, no Replace",      () => BuildDropdownStringBuilder(dossiers));
Run("OK: HtmlAgilityPack",                () => BuildDropdownFast(dossiers));

// ---------------------------------------------------------------------------

static void Run(string label, Func<string> render)
{
    // Warm up the JIT and stabilize the heap before measuring.
    for (int i = 0; i < 5; i++) _ = render();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

    long bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

    var sw = Stopwatch.StartNew();
    Parallel.For(0L, Iterations, new ParallelOptions { MaxDegreeOfParallelism = ConcurrentUsers },
        _ => { render(); });
    sw.Stop();

    long bytesAfter = GC.GetTotalAllocatedBytes(precise: true);
    int dg0 = GC.CollectionCount(0) - g0;
    int dg1 = GC.CollectionCount(1) - g1;
    int dg2 = GC.CollectionCount(2) - g2;
    double mb = (bytesAfter - bytesBefore) / 1024.0 / 1024.0;

    Console.WriteLine();
    Console.WriteLine($"[{label}]");
    Console.WriteLine($"  elapsed     : {sw.ElapsedMilliseconds,8} ms");
    Console.WriteLine($"  allocated   : {mb,8:N1} MB");
    Console.WriteLine($"  GC Gen0/1/2 : {dg0,4} / {dg1,4} / {dg2,4}");
}

// The pathological version: builds the HTML by string concatenation and
// fires string.Replace twice per option to inject a custom color. Each
// Replace returns a brand-new string. With 1500 options, that is 3000+
// throwaway strings *per render*, all of them medium-sized and many of
// them surviving long enough to make it out of Gen 0.
static string BuildDropdownSlow(Dossier[] dossiers)
{
    string html = "<select id=\"dossier\" class=\"form-control\">";
    foreach (var d in dossiers)
    {
        // Template with placeholders. In the original code this template
        // came from a static field, but the principle is the same.
        string optionTemplate =
            "<option value=\"__ID__\" style=\"color:__COLOR__;\">__LABEL__</option>";

        string color = ColorFor(d.Kind);

        // Two Replace calls -> two new strings allocated per option.
        // Then the result is concatenated into `html`, which itself reallocates.
        string option = optionTemplate
            .Replace("__ID__", d.Id.ToString())
            .Replace("__COLOR__", color)
            .Replace("__LABEL__", d.Label);

        html += option;   // <-- O(n^2) allocation pattern
    }
    html += "</select>";
    return html;
}

// The cheap fix: a pre-sized StringBuilder eliminates the O(n²) reallocation
// from +=, and direct Append calls remove Replace entirely. No extra library.
// Allocations drop dramatically; Gen 2 collections should fall to near zero.
static string BuildDropdownStringBuilder(Dossier[] dossiers)
{
    // Each option is ~70 bytes; pre-size to avoid internal resizes.
    var sb = new StringBuilder(dossiers.Length * 80);
    sb.Append("<select id=\"dossier\" class=\"form-control\">");
    foreach (var d in dossiers)
    {
        sb.Append("<option value=\"").Append(d.Id)
          .Append("\" style=\"color:").Append(ColorFor(d.Kind))
          .Append(";\">").Append(d.Label)
          .Append("</option>");
    }
    sb.Append("</select>");
    return sb.ToString();
}

// The fixed version: build a real DOM with HtmlAgilityPack, set attributes
// directly, no Replace, no string concatenation in the hot loop.
static string BuildDropdownFast(Dossier[] dossiers)
{
    var doc = new HtmlDocument();
    var select = doc.CreateElement("select");
    select.SetAttributeValue("id", "dossier");
    select.SetAttributeValue("class", "form-control");

    foreach (var d in dossiers)
    {
        var option = doc.CreateElement("option");
        option.SetAttributeValue("value", d.Id.ToString());
        option.SetAttributeValue("style", $"color:{ColorFor(d.Kind)};");
        option.AppendChild(doc.CreateTextNode(d.Label));
        select.AppendChild(option);
    }

    doc.DocumentNode.AppendChild(select);

    using var sw = new StringWriter();
    doc.Save(sw);
    return sw.ToString();
}

static string ColorFor(string kind) => kind switch
{
    "Active"   => "#1f8a3a",
    "Pending"  => "#c98a00",
    "Closed"   => "#666666",
    "Urgent"   => "#c0392b",
    "Archived" => "#2c3e50",
    _          => "#000000"
};

record Dossier(int Id, string Label, string Kind);
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiametroLineaDesktop.Services;

// Headless front end for the design engine: spec in, .flp out — same builder, same physics and
// same file format the GUI uses. Meant to be driven by a script or an agent.

Console.OutputEncoding = System.Text.Encoding.UTF8;   // Ø, ρ and → in the report

var json = new JsonSerializerOptions
{
    WriteIndented          = true,
    PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling    = JsonCommentHandling.Skip,
    AllowTrailingCommas    = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

try
{
    return (args.Length == 0 ? "help" : args[0].ToLowerInvariant()) switch
    {
        "build"   => Build(args),
        "inspect" => Inspect(args),
        "report"  => ReportOnly(args),
        _         => Help(),
    };
}
catch (DesignSpecException ex)
{
    Console.Error.WriteLine($"Spec error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}

int Help()
{
    Console.WriteLine("""
        flyline — headless fly line designer

          flyline build   <spec.json> [-o <out.flp>]   build a design and save it
          flyline inspect <in.flp>    [-o <spec.json>] recover the spec of an existing design
          flyline report  <in.flp>                     mass, AFFTA class and sink speeds

        Without -o, build writes next to the spec and inspect prints to stdout.
        """);
    return 0;
}

int Build(string[] a)
{
    string specPath = Arg(a, 1) ?? throw new ArgumentException("build needs a spec file: flyline build <spec.json>");
    var spec = JsonSerializer.Deserialize<LineDesignSpec>(File.ReadAllText(specPath), json)
               ?? throw new InvalidDataException("The spec file is empty or not valid JSON.");

    var project = LineDesignBuilder.Build(spec, out var notes);

    string outPath = Opt(a, "-o")
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(specPath)) ?? ".",
                        Sanitize(spec.Name) + ProjectService.FileExtension);
    ProjectService.Save(project, outPath);

    foreach (var n in notes) Console.WriteLine($"  · {n}");
    Console.WriteLine($"\nSaved: {outPath}\n");
    PrintReport(LineDesignBuilder.Report(project));
    return 0;
}

int Inspect(string[] a)
{
    string path = Arg(a, 1) ?? throw new ArgumentException("inspect needs a project file: flyline inspect <in.flp>");
    var project = ProjectService.Load(path);
    string text = JsonSerializer.Serialize(LineDesignBuilder.ToSpec(project), json);

    string? outPath = Opt(a, "-o");
    if (outPath is null) Console.WriteLine(text);
    else { File.WriteAllText(outPath, text); Console.WriteLine($"Saved: {outPath}"); }
    return 0;
}

int ReportOnly(string[] a)
{
    string path = Arg(a, 1) ?? throw new ArgumentException("report needs a project file: flyline report <in.flp>");
    PrintReport(LineDesignBuilder.Report(ProjectService.Load(path)));
    return 0;
}

void PrintReport(LineDesignBuilder.DesignReport r)
{
    Console.WriteLine($"{r.Name}");
    Console.WriteLine($"  length {r.TotalLengthCm:0.#} cm ({r.TotalLengthCm / 30.48:0.#} ft)   " +
                      $"head {r.HeadLengthCm:0.#} cm   mass {r.TotalMassGrains:0.0} gr   head {r.HeadMassGrains:0.0} gr");
    Console.WriteLine(r.AfftaClass > 0
        ? $"  AFFTA #{r.AfftaClass}  (first 30 ft = {r.Affta30FtGrains:0.0} gr)"
        : "  AFFTA — not classifiable (no density set)");
    Console.WriteLine();
    Console.WriteLine($"  {"#",-3}{"name",-16}{"start",8}{"end",8}{"Ø start",9}{"Ø end",9}{"ρ",8}{"mass g",9}  sink");
    int i = 1;
    foreach (var s in r.Sections)
        Console.WriteLine($"  {i++,-3}{Trim(s.Name, 15),-16}{s.StartCm,8:0.#}{s.EndCm,8:0.#}" +
                          $"{s.StartDiamMm,9:0.000}{s.EndDiamMm,9:0.000}{s.DensityGCm3,8:0.00}{s.MassG,9:0.000}  {s.SinkText}");
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..n];

static string? Arg(string[] a, int i) => i < a.Length && !a[i].StartsWith('-') ? a[i] : null;

static string? Opt(string[] a, string name)
{
    int i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static string Sanitize(string name)
{
    foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    return name;
}

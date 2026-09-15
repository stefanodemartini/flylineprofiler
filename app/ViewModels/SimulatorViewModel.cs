using System.IO;
using DiametroLineaDesktop.Helpers;
using DiametroLineaDesktop.Models;
using DiametroLineaDesktop.Physics;
using DiametroLineaDesktop.Services;

namespace DiametroLineaDesktop.ViewModels;

/// <summary>
/// Backs the Simulator window: loads one or two .flp projects read-only, runs the casting physics
/// (app/Physics/) for the selected pre-registered scenario, and holds the results the view renders
/// (2D loop viewer + the three comparison charts). No UI/WPF types here — the view owns all of that.
/// </summary>
public class SimulatorViewModel : ObservableObject
{
    public SimulationSettings Settings { get; } = new();

    private string _v1FileName = "(nessuna)";
    private string _v2FileName = "(nessuna)";
    private string _statusText = "Carica almeno Linea 1 per iniziare.";
    private CastingScenarioKind _selectedScenario = CastingScenarioKind.OverheadCast;

    public string V1FileName { get => _v1FileName; private set => SetProperty(ref _v1FileName, value); }
    public string V2FileName { get => _v2FileName; private set => SetProperty(ref _v2FileName, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public CastingScenarioKind SelectedScenario
    {
        get => _selectedScenario;
        set => SetProperty(ref _selectedScenario, value);
    }

    public FlyLineProject? ProjectV1 { get; private set; }
    public FlyLineProject? ProjectV2 { get; private set; }
    public CastSimulationResult? ResultV1 { get; private set; }
    public CastSimulationResult? ResultV2 { get; private set; }

    public bool HasV1 => ProjectV1 != null;
    public bool HasV2 => ProjectV2 != null;

    public bool LoadV1(string path) => Load(path, p => ProjectV1 = p, n => V1FileName = n);
    public bool LoadV2(string path) => Load(path, p => ProjectV2 = p, n => V2FileName = n);

    private bool Load(string path, Action<FlyLineProject> setProject, Action<string> setName)
    {
        try
        {
            var project = ProjectService.Load(path);
            setProject(project);
            setName(Path.GetFileName(path));
            StatusText = $"Caricato: {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Errore caricamento: {ex.Message}";
            return false;
        }
    }

    /// <summary>Runs the selected scenario for every loaded project. Synchronous — a few thousand
    /// time steps at the default settings complete well within an interactive wait, and the caller
    /// (the view) is expected to run this off the UI thread for a busy-cursor experience.</summary>
    public void Run()
    {
        if (ProjectV1 is null && ProjectV2 is null)
        {
            StatusText = "Nessuna linea caricata.";
            return;
        }
        var scenario = BuildScenario(SelectedScenario);
        ResultV1 = ProjectV1 is not null ? CastSimulator.Run(ProjectV1, scenario, Settings) : null;
        ResultV2 = ProjectV2 is not null ? CastSimulator.Run(ProjectV2, scenario, Settings) : null;
        StatusText = $"Simulazione completata — {scenario.NameIt}.";
    }

    private static CastingScenario BuildScenario(CastingScenarioKind kind) => kind switch
    {
        CastingScenarioKind.Distance => CastingScenario.Distance(),
        _ => CastingScenario.OverheadCast(),
    };

    /// <summary>Resolves the nozzle color (hex, no '#') that applies at arc length s (cm) — the same
    /// zone/base resolution order the rest of the app uses (a covering zone's nozzle, else M1/base).</summary>
    public static string NozzleColorHexAt(FlyLineProject project, double sCm)
    {
        foreach (var z in project.NozzleZones)
            if (sCm >= z.StartCm && sCm < z.EndCm && z.NozzleIndex >= 0 && z.NozzleIndex < project.NozzleDefinitions.Count)
                return project.NozzleDefinitions[z.NozzleIndex].ColorHex;
        return project.NozzleDefinitions.Count > 0 ? project.NozzleDefinitions[0].ColorHex : project.DesignLineColorHex;
    }
}

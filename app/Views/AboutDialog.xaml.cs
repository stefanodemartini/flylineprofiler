using System.Reflection;
using System.Windows;

namespace DiametroLineaDesktop.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        PopulateInfo();
    }

    private void PopulateInfo()
    {
        var asm = Assembly.GetExecutingAssembly();

        // Version (from csproj <Version>)
        var ver = asm.GetName().Version;
        VersionText.Text = ver is not null
            ? $"Version {ver.Major}.{ver.Minor}.{ver.Build}"
            : "Version 1.0";

        // Author
        var author = asm.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";
        CompanyText.Text = author;

        // Copyright
        var copy = asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        CopyrightText.Text = copy;

        // Author line  (no dedicated attribute — use product or hard-code)
        AuthorText.Text = "Author: Stefano De Martini";
    }

    private void OkBtn_Click(object sender, RoutedEventArgs e) => Close();
}

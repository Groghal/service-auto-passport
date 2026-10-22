using System.Windows.Forms;

namespace ServiceAutoPassport;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "ServiceAutoPassport",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

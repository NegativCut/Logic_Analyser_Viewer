internal static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Form1());
        }
        catch (Exception ex)
        {
            File.WriteAllText("laviewer_error.txt", ex.ToString());
            MessageBox.Show(ex.ToString(), "Startup Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

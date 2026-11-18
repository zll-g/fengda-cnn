namespace Fengda.Cnn.Training.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrainingMainForm());
    }
}

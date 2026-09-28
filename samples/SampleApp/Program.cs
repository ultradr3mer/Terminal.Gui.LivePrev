using Terminal.Gui.App;

using IApplication app = Application.Create();
app.Init();

using var view = new SampleApp.MainView();
app.Run(view);
return 0;

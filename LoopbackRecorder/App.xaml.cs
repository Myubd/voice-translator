using System;
using System.Windows;
using System.Windows.Threading;

namespace LoopbackRecorder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UIスレッド上のイベントハンドラ(ボタンのClickなど)で想定外の例外が発生すると、
        // 以前はどこにもログが残らずアプリが無言で落ちる可能性があった。
        // Loggerクラスがせっかくあるので、最後の砦としてここでキャッチしてログに残す。
        // (StartStopButton_Clickのtry/catchなど個別箇所のケアはそのまま活かしつつ、
        // それ以外の予期しない箇所を最終的に拾うための一枚として被せる)
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // UIスレッド以外(バックグラウンドタスク等)で発生し、誰にもcatchされなかった例外もログする。
        // こちらはプロセスの続行が保証されない(isTerminatingがtrueの場合、ログ後にプロセスは終了する)。
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // Task内で発生し、awaitもContinueWithでの観測もされなかった例外(未観測タスク例外)もログする。
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Log("App", "UIスレッドで未処理の例外が発生しました。", e.Exception);

        MessageBox.Show(
            $"予期しないエラーが発生しました。ログに詳細を記録しました。\n\n{e.Exception.Message}",
            "エラー",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // アプリを可能な限り継続させる(ここで落とすと作業中の録音・履歴が失われるため)。
        // ログに残るので、繰り返し発生する場合は原因調査ができる。
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Logger.Log("App", $"未処理の例外が発生しました(プロセス終了予定: {e.IsTerminating})。", ex);
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Logger.Log("App", "観測されなかったタスク例外が発生しました。", e.Exception);
        // プロセスを巻き込んで落とさないよう観測済みにする
        e.SetObserved();
    }
}

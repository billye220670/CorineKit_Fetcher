using System.Windows;

namespace Fetcher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 上一次异常退出可能把 webm 的 .mkv 替身留在缓存目录里，启动时清一遍。
        // 替身不是硬链接就是副本，删掉动不到源文件。
        ImageSelector.VideoSourceResolver.PurgeAliases();
        base.OnStartup(e);
    }
}

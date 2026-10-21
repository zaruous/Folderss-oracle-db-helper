using Folderss.Plugins;
using System.Windows;

namespace MyPlugin
{
    /// <summary>
    /// 플러그인 진입점. plugin.json의 "type"(네임스페이스.클래스)과 이름이 같아야 한다.
    /// public이고 인수 없는 public 생성자가 있어야 한다.
    /// </summary>
    public sealed class MyPlugin : IFolderssPlugin
    {
        private IPluginManager _manager;

        /// <summary>처음 실행할 때 한 번 호출된다.</summary>
        public void Initialize(IPluginManager manager)
        {
            _manager = manager;

            manager.AddSettingsPage(new ConnectionSettingsPage(manager));
            manager.AddSettingsPage(new MonitorSettingsPage(manager));
            manager.AddSettingsPage(new SqlEditorSettingsPage(manager));
        }

        /// <summary>
        /// ⋯ 메뉴 > 플러그인에서 실행할 때마다 호출된다. 돌려준 요소가 팝업 창의 내용이 된다.
        /// 매번 새 요소를 만들어야 한다 (이미 다른 창에 붙은 요소를 다시 돌려주면 WPF 예외).
        /// 팝업마다 DB 연결을 따로 연다(팝업 사이에 연결을 나누지 않음).
        /// </summary>
        public FrameworkElement CreateView()
        {
            return new DbHelperView(_manager);
        }
    }
}

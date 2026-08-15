using System.Windows.Controls;
using ExHyperV.ViewModels;
using ExHyperV.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;

namespace ExHyperV.Views
{
    public partial class VirtualMachinesPage : Page
    {
        private bool _isSynchronizingVmSelection;

        public VirtualMachinesPage()
        {
            InitializeComponent();
            this.DataContext = new VirtualMachinesPageViewModel(new VmQueryService());
        }

        // OS 类型下拉：ComboBox 无 Command，改选后复用 ChangeOsTypeCommand 把 [OSType:] 落地到 WMI Notes。
        // SelectedItem 走 OneWay，命令是 OsType 的唯一写者；命令内部的相等守卫挡掉加载/切换虚拟机时的空触发。
        private void OnOsTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox { SelectedItem: string osType }
                && DataContext is VirtualMachinesPageViewModel vm
                && vm.ChangeOsTypeCommand.CanExecute(osType))
            {
                vm.ChangeOsTypeCommand.Execute(osType);
            }
        }

        // ListView 多选（Ctrl/Shift）无法直接绑定 SelectedItems，经此把选中集推给 VM：>1 时右键菜单收敛为删除/彻底删除。
        private void VmList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSynchronizingVmSelection
                || e.AddedItems.Count == 0
                || sender is not System.Windows.Controls.ListView lv
                || DataContext is not VirtualMachinesPageViewModel vm)
                return;

            _isSynchronizingVmSelection = true;
            try
            {
                foreach (System.Windows.Controls.ListView other in FindVisualChildren<System.Windows.Controls.ListView>(HostGroupsList))
                {
                    if (!ReferenceEquals(other, lv)) other.UnselectAll();
                }
                vm.UpdateSelection(lv.SelectedItems);
            }
            finally
            {
                _isSynchronizingVmSelection = false;
            }
        }

        private void VmList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListView list
                || e.OriginalSource is not DependencyObject source)
                return;

            if (ItemsControl.ContainerFromElement(list, source) is System.Windows.Controls.ListViewItem item)
                item.IsSelected = true;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is T match) yield return match;
                foreach (T descendant in FindVisualChildren<T>(child)) yield return descendant;
            }
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;

namespace ExHyperV.Interaction
{
    internal static class ConsoleDisplayModeDialogLayout
    {
        internal static void EnsureButtonsFit(ContentDialog dialog)
        {
            ArgumentNullException.ThrowIfNull(dialog);

            RoutedEventHandler? loadedHandler = null;
            EventHandler? layoutHandler = null;

            void Detach()
            {
                if (loadedHandler is not null)
                    dialog.Loaded -= loadedHandler;
                if (layoutHandler is not null)
                    dialog.LayoutUpdated -= layoutHandler;
            }

            void ApplyWhenReady()
            {
                if (!ApplyButtonMinimumWidths(dialog)) return;
                Detach();
                dialog.InvalidateMeasure();
            }

            loadedHandler = (_, _) => ApplyWhenReady();
            layoutHandler = (_, _) => ApplyWhenReady();
            dialog.Loaded += loadedHandler;
            dialog.LayoutUpdated += layoutHandler;
            ApplyWhenReady();
        }

        private static bool ApplyButtonMinimumWidths(ContentDialog dialog)
        {
            var labels = new HashSet<string>(StringComparer.Ordinal)
            {
                dialog.PrimaryButtonText,
                dialog.SecondaryButtonText,
                dialog.CloseButtonText
            };
            labels.RemoveWhere(string.IsNullOrEmpty);
            int appliedCount = 0;

            foreach (Button button in FindVisualChildren<Button>(dialog))
            {
                if (button.Content is not string label || !labels.Contains(label))
                    continue;

                button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double minWidth = Math.Ceiling(
                    button.DesiredSize.Width - button.Margin.Left - button.Margin.Right);
                button.MinWidth = Math.Max(button.MinWidth, minWidth);

                if (button.Parent is Grid footerGrid)
                {
                    int column = Grid.GetColumn(button);
                    if (column >= 0 && column < footerGrid.ColumnDefinitions.Count)
                    {
                        footerGrid.ColumnDefinitions[column].MinWidth = Math.Max(
                            footerGrid.ColumnDefinitions[column].MinWidth,
                            minWidth);
                    }
                }

                appliedCount++;
            }

            return appliedCount == labels.Count;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is T typed)
                    yield return typed;
                foreach (T descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }
    }
}

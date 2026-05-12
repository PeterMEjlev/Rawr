using System.Windows;
using System.Windows.Controls;

namespace Rawr.App.Controls;

// Per-item DataTemplate switch for the photo grid: photos render as the usual
// thumbnail tile, DateHeaderItems render as a full-width separator. Selecting
// by runtime type avoids fighting WPF's implicit type-keyed templating, which
// the explicit ItemTemplate setter on the grid would override.
public sealed class GridItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PhotoTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        => item is IGridFullWidthItem ? HeaderTemplate : PhotoTemplate;
}

// Headers participate in layout but not selection/focus — without this, keyboard
// navigation through the grid would land on them.
public sealed class GridItemContainerStyleSelector : StyleSelector
{
    public Style? PhotoStyle { get; set; }
    public Style? HeaderStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
        => item is IGridFullWidthItem ? HeaderStyle : PhotoStyle;
}

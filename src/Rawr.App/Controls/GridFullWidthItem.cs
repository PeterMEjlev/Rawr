namespace Rawr.App.Controls;

// Marker for grid items that should occupy a full row instead of a single cell
// when laid out by VirtualizingWrapPanel. The panel checks for this interface
// to decide row breaks; the renderer chooses the DataTemplate by item type.
public interface IGridFullWidthItem
{
}

public sealed class DateHeaderItem : IGridFullWidthItem
{
    public DateHeaderItem(System.DateTime date, string label)
    {
        Date = date;
        Label = label;
    }

    public System.DateTime Date { get; }
    public string Label { get; }
}

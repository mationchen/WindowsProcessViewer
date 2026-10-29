using System.ComponentModel;
using System.Globalization;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 可排序的 BindingList：DataGridView 点列头即可排序（原生 BindingList 不支持）。
/// </summary>
public sealed class SortableBindingList<T> : BindingList<T>
{
    private bool _isSorted;
    private PropertyDescriptor? _sortProperty;
    private ListSortDirection _direction = ListSortDirection.Ascending;

    protected override bool SupportsSortingCore => true;
    protected override bool IsSortedCore => _isSorted;
    protected override PropertyDescriptor? SortPropertyCore => _sortProperty;
    protected override ListSortDirection SortDirectionCore => _direction;

    /// <summary>供界面调用（BindingList 的 ApplySort 是显式接口实现，不能直接调）。</summary>
    public void SortBy(PropertyDescriptor property, ListSortDirection direction)
        => ApplySortCore(property, direction);

    protected override void ApplySortCore(PropertyDescriptor prop, ListSortDirection direction)
    {
        if (Items is not List<T> list)
            return;

        var comparer = Comparer<object?>.Create(CompareValues);
        list.Sort((x, y) =>
        {
            var result = comparer.Compare(prop.GetValue(x), prop.GetValue(y));
            return direction == ListSortDirection.Ascending ? result : -result;
        });

        _sortProperty = prop;
        _direction = direction;
        _isSorted = true;
        OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
    }

    protected override void RemoveSortCore()
    {
        _isSorted = false;
        _sortProperty = null;
        OnListChanged(new ListChangedEventArgs(ListChangedType.Reset, -1));
    }

    private static int CompareValues(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a is null)
            return 1;
        if (b is null)
            return -1;

        if (a is string sa && b is string sb)
            return string.Compare(sa, sb, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);

        if (a is IComparable && a.GetType() == b.GetType())
            return ((IComparable)a).CompareTo(b);

        if (IsNumeric(a) && IsNumeric(b))
            return Convert.ToDouble(a, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));

        return string.Compare(a.ToString(), b.ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}

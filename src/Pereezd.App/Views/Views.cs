using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Pereezd.App.Views;

public partial class HomeView : UserControl { public HomeView() => InitializeComponent(); }
public partial class CollectView : UserControl { public CollectView() => InitializeComponent(); }
public partial class CollectJobView : UserControl { public CollectJobView() => InitializeComponent(); }
public partial class CollectDoneView : UserControl { public CollectDoneView() => InitializeComponent(); }
public partial class RestoreView : UserControl { public RestoreView() => InitializeComponent(); }
public partial class RestoreJobView : UserControl { public RestoreJobView() => InitializeComponent(); }
public partial class RestoreDoneView : UserControl { public RestoreDoneView() => InitializeComponent(); }

public sealed class Invert : IValueConverter
{
    public static readonly Invert Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

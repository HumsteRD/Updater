using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using Pereezd.Core;

namespace Pereezd.App;

public static class UiCommands
{
    /// <summary>Открыть ссылку от имени пользователя (браузер не получит прав администратора).</summary>
    public static ICommand OpenLink { get; } = new RelayCommand(p =>
    {
        if (p is string s && s.Length > 0) Proc.OpenAsUser(s);
    });
}

/// <summary>Строка с флажком — для того, что можно выбрать; без флажка — для подсказок и списков.</summary>
public sealed class ItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Selectable { get; set; }
    public DataTemplate? Note { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is PlanItem { Selectable: true } ? Selectable : Note;
}

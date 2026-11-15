using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace OrgExplorer.Desktop;

public sealed class ProfileCopyText : TextBlock
{
    static ProfileCopyText() => AutomationProperties.HelpTextProperty.OverrideMetadata(
        typeof(ProfileCopyText), new FrameworkPropertyMetadata("", (sender, _) => ((ProfileCopyText)sender).UpdateCopyHint()));

    public ProfileCopyText()
    {
        var copy = new MenuItem { Header = "Copy value" };
        copy.SetBinding(IsEnabledProperty, new Binding(nameof(CanCopy)) { Source = this });
        copy.SetBinding(TagProperty, new Binding(nameof(CopyText)) { Source = this });
        copy.Click += (_, _) => RequestCopy();
        ContextMenu = new ContextMenu { Items = { copy } };
    }

    public static readonly DependencyProperty CopyTextProperty = DependencyProperty.Register(
        nameof(CopyText), typeof(string), typeof(ProfileCopyText), new PropertyMetadata(null, CopyTextChanged));
    public static readonly DependencyProperty CopyLabelProperty = DependencyProperty.Register(
        nameof(CopyLabel), typeof(string), typeof(ProfileCopyText), new PropertyMetadata("Value"));
    private static readonly DependencyPropertyKey CanCopyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanCopy), typeof(bool), typeof(ProfileCopyText), new PropertyMetadata(false));
    public static readonly DependencyProperty CanCopyProperty = CanCopyPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey CopyHintPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CopyHint), typeof(string), typeof(ProfileCopyText), new PropertyMetadata(null));
    public static readonly DependencyProperty CopyHintProperty = CopyHintPropertyKey.DependencyProperty;

    public string? CopyText
    {
        get => (string?)GetValue(CopyTextProperty);
        set => SetValue(CopyTextProperty, value);
    }
    public bool CanCopy => (bool)GetValue(CanCopyProperty);
    public string? CopyHint => (string?)GetValue(CopyHintProperty);
    public string CopyLabel
    {
        get => (string)GetValue(CopyLabelProperty);
        set => SetValue(CopyLabelProperty, value);
    }

    public static readonly RoutedEvent CopyRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CopyRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ProfileCopyText));

    public event RoutedEventHandler CopyRequested
    {
        add => AddHandler(CopyRequestedEvent, value);
        remove => RemoveHandler(CopyRequestedEvent, value);
    }

    private static void CopyTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var value = (ProfileCopyText)sender;
        value.SetValue(CanCopyPropertyKey, !string.IsNullOrEmpty(value.CopyText));
        value.Cursor = value.CanCopy ? Cursors.Hand : null;
        AutomationProperties.SetItemStatus(value, value.CanCopy ? "Click to copy" : "");
        value.UpdateCopyHint();
    }

    private void UpdateCopyHint()
    {
        var owner = AutomationProperties.GetHelpText(this);
        SetValue(CopyHintPropertyKey, CanCopy
            ? "Click to copy\n" + CopyText + (string.IsNullOrEmpty(owner) ? "" : "\n" + owner)
            : null);
    }

    private void RequestCopy()
    {
        if (CanCopy && IsEnabled) RaiseEvent(new RoutedEventArgs(CopyRequestedEvent));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!CanCopy || !IsEnabled) return;
        if (IsVisible) Focus();
        RequestCopy();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space) || !CanCopy || !IsEnabled) return;
        e.Handled = true;
        if (!e.IsRepeat) RequestCopy();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CopyPeer(this);

    private sealed class CopyPeer(ProfileCopyText owner) : TextBlockAutomationPeer(owner), IInvokeProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        protected override string GetLocalizedControlTypeCore() => "copyable text";
        protected override bool IsEnabledCore() => owner.CanCopy && base.IsEnabledCore();

        public void Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(owner.RequestCopy, DispatcherPriority.Input);
        }
    }
}

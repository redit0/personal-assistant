using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Assistant.Core.Tools;
using Assistant.Tools.Catalog;

namespace Assistant.App;

/// <summary>
/// Asks you to approve one tool call. It opens on top without taking focus and has no default button, so keys you
/// were typing in another window keep going there instead of pressing Approve or Deny. Only a click decides.
/// Closing the window or letting the countdown run out counts as Deny.
/// </summary>
public partial class ApprovalWindow : Window
{
    private readonly DispatcherTimer _timer;
    private DateTime _deadline;

    public ApprovalWindow(ApprovalRequest request, TimeSpan timeout)
    {
        InitializeComponent();
        TitleBarIcon.Attach(this);

        Heading.Text = $"{request.Caller.DisplayName} wants to {request.ToolTitle.ToLowerInvariant()}";
        Subheading.Text = $"Tool: {request.ToolName}    Kind: {CategoryLabel(request.Category)}    Caller: {request.Caller.Id}";
        ArgumentsText.Text = Pretty(request.ArgumentsJson);

        if (request.Reason is not null)
        {
            ReasonText.Text = request.Reason;
            ReasonBox.Visibility = Visibility.Visible;
        }

        _deadline = DateTime.UtcNow + timeout;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();

        Closed += (_, _) => _timer.Stop();
    }

    public bool Approved { get; private set; }

    private void Tick()
    {
        var left = _deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            Close();
            return;
        }

        Countdown.Text = $"Denies automatically in {(int)left.TotalSeconds}s";
    }

    private void OnApprove(object sender, RoutedEventArgs e)
    {
        Approved = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e) => Close();

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string CategoryLabel(ToolCategory category) => category switch
    {
        ToolCategory.Read => "reads data",
        ToolCategory.ItemManagement => "changes your items",
        _ => "acts outside the assistant",
    };
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DbCowboy;

/// <summary>
/// The New connection / Edit connection dialog: every setting as a labelled row on the left, and on the right
/// the connection as it will appear with a staged check that says how far a connection attempt got.
/// </summary>
public sealed partial class MainWindow
{
    enum EditorOutcome { Cancel, Save, Connect }

    static readonly string[] TlsModes = ["VerifyFull", "VerifyCA", "Require", "Prefer", "Disable"];
    static readonly string[] TlsLabels =
    [
        "Verify full · encrypt, check certificate and host name",
        "Verify CA · encrypt, check certificate only",
        "Require · encrypt, no identity check",
        "Prefer · encrypt if the server offers it",
        "Disable · no encryption"
    ];

    /// <summary>A labelled form row. The returned setter shows an error under the field, or clears it with null.</summary>
    static (Grid Row, Action<string?> Error) FormRow(string label, FrameworkElement field)
    {
        var caption = Muted(label, 12);
        caption.HorizontalAlignment = HorizontalAlignment.Right; caption.VerticalAlignment = VerticalAlignment.Top; caption.Margin = new Thickness(0, 7, 0, 0); caption.TextWrapping = TextWrapping.NoWrap;
        var error = Themed(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }, (e, dark) => { if (!HighContrast) e.Foreground = new SolidColorBrush(ToneColor(Tone.Warn, dark)); });
        var cell = new StackPanel { Spacing = 3 }; cell.Children.Add(field); cell.Children.Add(error);
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(88) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(caption); Grid.SetColumn(cell, 1); row.Children.Add(cell);
        if (label.Length > 0 && AutomationProperties.GetName(field).Length == 0) AutomationProperties.SetName(field, label);
        return (row, message => { error.Text = message ?? ""; error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible; });
    }

    /// <summary>A quiet tinted panel that holds one group of form rows.</summary>
    static Border FormGroup(params UIElement[] rows)
    {
        var stack = new StackPanel { Spacing = 8 }; foreach (var row in rows) stack.Children.Add(row);
        var group = new Border { Child = stack, CornerRadius = new CornerRadius(10), Padding = new Thickness(0, 12, 12, 12), BorderThickness = new Thickness(1) };
        return Themed(group, (g, dark) =>
        {
            if (HighContrast) { g.ClearValue(Border.BackgroundProperty); g.BorderBrush = SystemText(); return; }
            g.Background = new SolidColorBrush(Brand.Fade(dark ? Brand.Silver : Brand.Line, dark ? (byte)16 : (byte)18)); g.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        });
    }

    async Task<string?> PickFile(params string[] extensions)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            picker.FileTypeFilter.Add("*");
            return (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception) { return null; }
    }

    async Task<bool> EditConnection(Profile? prior, bool asNew = false)
    {
        if (operation != null || dialogOpen || connectingProfile != null) return false;
        bool isNew = prior == null || asNew;
        var p = prior ?? new Profile { Name = "", Ssl = "Disable" };

        // ---- What the connection is to you -------------------------------------------------------------
        var name = new TextBox { Text = p.Name, MaxLength = 80 };
        int art = Math.Max(0, Array.IndexOf(IdentityNames, p.Identity));
        var artInitial = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 };
        var artTiles = IdentityNames.Select((identity, i) =>
        {
            var tile = new ToggleButton { Width = 36, Height = 36, Padding = new Thickness(0), Content = i == 0 ? artInitial : new Image { Width = 26, Height = 26, Source = Asset(IdentityFiles[i]) } };
            AutomationProperties.SetName(tile, i == 0 ? "No art, show the initial" : identity + " art"); ToolTipService.SetToolTip(tile, i == 0 ? "Initial" : identity);
            return BrandControl(tile);
        }).ToArray();
        var tag = new ComboBox { ItemsSource = new[] { "No tag", "Local", "Development", "Staging", "Production" }, SelectedIndex = Math.Max(0, Array.IndexOf(Profile.Tags, p.Production ? "Production" : p.Tag)), MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(tag, "Tag");
        var tagLabel = Muted("Tag", 12); tagLabel.VerticalAlignment = VerticalAlignment.Center; tagLabel.Margin = new Thickness(8, 0, 0, 0);
        var artRow = new Grid { ColumnSpacing = 6 };
        foreach (var _ in artTiles) artRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        artRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); artRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < artTiles.Length; i++) { Grid.SetColumn(artTiles[i], i); artRow.Children.Add(artTiles[i]); }
        Grid.SetColumn(tagLabel, artTiles.Length); artRow.Children.Add(tagLabel); Grid.SetColumn(tag, artTiles.Length + 1); artRow.Children.Add(tag);
        var safety = new ComboBox { ItemsSource = new[] { "Read-only · writes are blocked until you turn it off", "Ask first · every write shows the statement before it runs" }, SelectedIndex = p.ReadOnly ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };

        // ---- How to reach it ---------------------------------------------------------------------------
        var url = new TextBox { PlaceholderText = "Paste postgres://… to fill the rows below", FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        var host = new TextBox { Text = p.Host };
        var port = new NumberBox { Value = p.Port, Minimum = 1, Maximum = 65535, Width = 92, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        AutomationProperties.SetName(port, "Port");
        var portLabel = Muted("Port", 12); portLabel.VerticalAlignment = VerticalAlignment.Center;
        var hostRow = new Grid { ColumnSpacing = 8 };
        hostRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); hostRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); hostRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        hostRow.Children.Add(host); Grid.SetColumn(portLabel, 1); hostRow.Children.Add(portLabel); Grid.SetColumn(port, 2); hostRow.Children.Add(port);
        AutomationProperties.SetName(host, "Host");
        var user = new TextBox { Text = p.Username };
        var password = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek };
        AutomationProperties.SetName(password, "Password");
        var store = new ComboBox { ItemsSource = new[] { "Save for this account", "This session only", "Ask every time" }, SelectedIndex = p.AskPassword ? 2 : p.Secret.Length > 0 || isNew ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(store, "Where the password is kept");
        ToolTipService.SetToolTip(store, "Saved passwords are protected for your Windows account. A session password is forgotten when DB Cowboy closes.");
        var passwordRow = new Grid { ColumnSpacing = 8 };
        passwordRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); passwordRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        passwordRow.Children.Add(password); Grid.SetColumn(store, 1); passwordRow.Children.Add(store);
        var database = new TextBox { Text = p.Database };
        var tls = new ComboBox { ItemsSource = TlsLabels, SelectedIndex = Math.Max(0, Array.IndexOf(TlsModes, p.Ssl)), HorizontalAlignment = HorizontalAlignment.Stretch };
        string keyFile = p.ClientKey, certFile = p.ClientCertificate, caFile = p.RootCertificate;
        Button CertButton(string label) { var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 6, 6, 6) }; AutomationProperties.SetName(b, label); return BrandControl(b); }
        Button keyButton = CertButton("Client key"), certButton = CertButton("Client certificate"), caButton = CertButton("CA certificate");
        var clearCerts = new Button { Content = "−", Padding = new Thickness(11, 6, 11, 6) };
        AutomationProperties.SetName(clearCerts, "Clear certificate files"); ToolTipService.SetToolTip(clearCerts, "Clear certificate files");
        var certRow = new Grid { ColumnSpacing = 6 };
        foreach (var _ in new[] { 0, 1, 2 }) certRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        certRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        certRow.Children.Add(keyButton); Grid.SetColumn(certButton, 1); certRow.Children.Add(certButton); Grid.SetColumn(caButton, 2); certRow.Children.Add(caButton); Grid.SetColumn(clearCerts, 3); certRow.Children.Add(clearCerts);
        var certNote = Muted("", 12);

        // ---- Over SSH ----------------------------------------------------------------------------------
        var sshHost = new TextBox { Text = p.SshHost, PlaceholderText = "bastion.example.com" };
        AutomationProperties.SetName(sshHost, "SSH host");
        var sshPort = new NumberBox { Value = p.SshPort, Minimum = 1, Maximum = 65535, Width = 92, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
        AutomationProperties.SetName(sshPort, "SSH port");
        var sshPortLabel = Muted("Port", 12); sshPortLabel.VerticalAlignment = VerticalAlignment.Center;
        var sshHostRow = new Grid { ColumnSpacing = 8 };
        sshHostRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); sshHostRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); sshHostRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sshHostRow.Children.Add(sshHost); Grid.SetColumn(sshPortLabel, 1); sshHostRow.Children.Add(sshPortLabel); Grid.SetColumn(sshPort, 2); sshHostRow.Children.Add(sshPort);
        var sshUser = new TextBox { Text = p.SshUser };
        var sshPassword = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek, PlaceholderText = p.SshSecret.Length > 0 ? "Saved · type to replace" : "" };
        string sshKeyFile = p.SshKeyFile; bool sshSecretChanged = false;
        var sshKeyButton = CertButton("SSH private key"); var sshKeyClear = new Button { Content = "−", Padding = new Thickness(11, 6, 11, 6) };
        AutomationProperties.SetName(sshKeyClear, "Clear the SSH private key");
        var sshSecretRow = new Grid { ColumnSpacing = 8 };
        sshSecretRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); sshSecretRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); sshSecretRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        sshSecretRow.Children.Add(sshPassword); Grid.SetColumn(sshKeyButton, 1); sshSecretRow.Children.Add(sshKeyButton); Grid.SetColumn(sshKeyClear, 2); sshSecretRow.Children.Add(sshKeyClear);
        string hostKey = p.SshHostKey, hostKeyHost = p.SshHost; int hostKeyPort = p.SshPort;
        var hostKeyNote = Muted("", 12); hostKeyNote.IsTextSelectionEnabled = true;
        var forgetHostKey = new HyperlinkButton { Content = "Forget host key", Padding = new Thickness(0), FontSize = 12 };
        var sshNote = Muted("The database host and port above are looked up from the SSH server. The SSH password or passphrase is saved for this Windows account.", 12);

        // ---- Rows and groups ---------------------------------------------------------------------------
        var (nameRow, _) = FormRow("Name", name);
        var (artFormRow, _) = FormRow("Art", artRow);
        var (safetyRow, _) = FormRow("Safety", safety);
        var urlNote = Muted("", 12); urlNote.Visibility = Visibility.Collapsed;
        var urlCell = Stack(url, urlNote); urlCell.Spacing = 3;
        var (urlRow, _) = FormRow("URL", urlCell);
        var (hostFormRow, hostError) = FormRow("Host", hostRow);
        var (userRow, userError) = FormRow("User", user);
        var (passwordFormRow, passwordError) = FormRow("Password", passwordRow);
        var (databaseRow, databaseError) = FormRow("Database", database);
        var (tlsRow, _) = FormRow("TLS mode", tls);
        var certCell = Stack(certRow, certNote); certCell.Spacing = 4;
        var (certFormRow, certError) = FormRow("", certCell);
        var (sshHostFormRow, sshHostError) = FormRow("SSH host", sshHostRow);
        var (sshUserRow, sshUserError) = FormRow("SSH user", sshUser);
        var (sshSecretFormRow, sshSecretError) = FormRow("Password", sshSecretRow);
        var (sshKeyNoteRow, _) = FormRow("", Stack(Row(hostKeyNote, forgetHostKey), sshNote));
        var sshGroup = FormGroup(sshHostFormRow, sshUserRow, sshSecretFormRow, sshKeyNoteRow);
        var form = Stack(FormGroup(nameRow, artFormRow, safetyRow), FormGroup(urlRow, hostFormRow, userRow, passwordFormRow, databaseRow, tlsRow, certFormRow), sshGroup);

        // ---- The right-hand panel: how it will appear, and the staged check ----------------------------
        var preview = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        var headline = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var stepList = new StackPanel { Spacing = 8 };
        var advice = Muted("", 12); advice.Visibility = Visibility.Collapsed;
        var fixes = new WrapRow([]);
        var panel = Stack(Eyebrow("How it will appear"), preview, Eyebrow("Connection check"), headline, stepList, advice, fixes);
        panel.Spacing = 8;
        // Screen readers hear the outcome of a check without moving focus.
        AutomationProperties.SetLiveSetting(headline, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        // ---- Footer ------------------------------------------------------------------------------------
        var overSsh = BrandControl(new ToggleButton { Content = "Over SSH", IsChecked = p.Ssh, Padding = new Thickness(13, 7, 13, 7) });
        ToolTipService.SetToolTip(overSsh, "Reach the database through an SSH server");
        ContentDialog dialog = null!; var outcome = EditorOutcome.Cancel; CancellationTokenSource? checking = null; CheckResult? last = null;
        // In a narrow window the check sits under the form, so it is scrolled into view when it has something to say.
        Border? stackedCheck = null;
        var cancelButton = Button("Cancel", (_, _) => { checking?.Cancel(); dialog.Hide(); });
        Button saveButton = null!, testButton = null!, connectButton = null!;

        // ---- State -------------------------------------------------------------------------------------
        bool passwordChanged = false, tlsChosen = !isNew, filling = false;
        var note = "";
        try { password.Password = passwords.GetValueOrDefault(p.Id) ?? Store.Unprotect(p.Secret); } catch { note = "The saved password could not be read on this Windows account. Enter it again."; }
        int PortOf(NumberBox box, int fallback) => double.IsFinite(box.Value) && box.Value == Math.Truncate(box.Value) && box.Value is >= 1 and <= 65535 ? (int)box.Value : fallback;
        string AutoName() => ConnectionProfiles.DefaultName(database.Text, host.Text);

        // The profile without its secrets: enough for the preview and the plan of the check.
        Profile Draft() => p with
        {
            Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : AutoName(), Host = host.Text.Trim(), Port = PortOf(port, 5432), Database = database.Text.Trim(), Username = user.Text.Trim(),
            Ssl = TlsModes[Math.Max(0, tls.SelectedIndex)], RootCertificate = caFile, ClientCertificate = certFile, ClientKey = keyFile,
            AskPassword = store.SelectedIndex == 2, ReadOnly = safety.SelectedIndex == 0, Tag = Profile.Tags[Math.Max(0, tag.SelectedIndex)], Production = tag.SelectedIndex == 4, Identity = IdentityNames[art],
            Ssh = overSsh.IsChecked == true, SshHost = sshHost.Text.Trim(), SshPort = PortOf(sshPort, 22), SshUser = sshUser.Text.Trim(), SshKeyFile = sshKeyFile,
            SshHostKey = sshHost.Text.Trim() == hostKeyHost && PortOf(sshPort, 22) == hostKeyPort ? hostKey : ""
        };
        Profile Value() => Draft() with
        {
            Secret = store.SelectedIndex == 0 ? (!passwordChanged && p.Secret.Length > 0 ? p.Secret : Store.Protect(password.Password)) : "",
            SshSecret = sshSecretChanged ? (sshPassword.Password.Length > 0 ? Store.Protect(sshPassword.Password) : "") : p.SshSecret
        };

        void RenderSteps(IReadOnlyList<CheckStep> steps)
        {
            stepList.Children.Clear();
            foreach (var step in steps)
            {
                FrameworkElement mark;
                if (step.State == CheckState.Running) mark = new ProgressRing { Width = 14, Height = 14, IsActive = true };
                else
                {
                    var (glyph, tone) = step.State switch { CheckState.Ok => ("", Tone.Good), CheckState.Failed => ("", Tone.Warn), CheckState.Skipped => ("", Tone.Neutral), _ => ("", Tone.Neutral) };
                    mark = Themed(new FontIcon { Glyph = glyph, FontSize = 13 }, (icon, dark) => { if (!HighContrast) icon.Foreground = new SolidColorBrush(ToneColor(tone, dark)); });
                }
                mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 3, 0, 0); mark.Width = 16;
                var title = Text(step.Title, 12); title.TextWrapping = TextWrapping.Wrap;
                var text = new StackPanel { Spacing = 1 }; text.Children.Add(title);
                if (step.Detail.Length > 0) { var detail = Muted(step.Detail, 12); detail.IsTextSelectionEnabled = true; text.Children.Add(detail); }
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(mark); Grid.SetColumn(text, 1); row.Children.Add(text);
                string state = step.State switch { CheckState.Ok => "done", CheckState.Failed => "failed", CheckState.Running => "in progress", CheckState.Skipped => "skipped", _ => "not tried yet" };
                AutomationProperties.SetName(row, $"{step.Title}, {state}" + (step.Detail.Length > 0 ? ", " + step.Detail : ""));
                stepList.Children.Add(row);
            }
        }

        void RefreshPreview()
        {
            var draft = Draft();
            name.PlaceholderText = AutoName();
            artInitial.Text = draft.Name.Length > 0 ? draft.Name[..1].ToUpperInvariant() : "?";
            var title = Text(draft.Name, 13); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(title, draft.Name);
            var facts = Muted((draft.Username.Length > 0 ? draft.Username + " · " : "") + (draft.ReadOnly ? "read-only" : "writes ask first"), 12);
            facts.TextWrapping = TextWrapping.NoWrap; facts.TextTrimming = TextTrimming.CharacterEllipsis;
            var text = new StackPanel { Spacing = 2 }; text.Children.Add(title); text.Children.Add(facts);
            var pills = new WrapRow([]);
            if (draft.Tag.Length > 0) pills.Children.Add(Pill(draft.Tag, draft.Production ? Tone.Warn : Tone.Neutral));
            if (draft.Ssh) pills.Children.Add(Pill("SSH"));
            if (pills.Children.Count > 0) text.Children.Add(pills);
            var card = new Grid { ColumnSpacing = 10 };
            card.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); card.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            card.Children.Add(ConnectionIdentityArt(draft, 36)); Grid.SetColumn(text, 1); card.Children.Add(text);
            preview.Content = Surface(card, 10, 10);
        }

        void RefreshCerts()
        {
            void Show(Button button, string label, string path) { button.Content = path.Length > 0 ? label + " ✓" : label + "…"; ToolTipService.SetToolTip(button, path.Length > 0 ? path : "Choose a PEM file"); }
            Show(keyButton, "Key", keyFile); Show(certButton, "Cert", certFile); Show(caButton, "CA cert", caFile);
            var applied = new[] { ("Key", keyFile), ("Cert", certFile), ("CA cert", caFile) }.Where(f => f.Item2.Length > 0).Select(f => f.Item1 + ": " + System.IO.Path.GetFileName(f.Item2)).ToList();
            certNote.Text = applied.Count > 0 ? string.Join(" · ", applied) : "No certificate files applied. A CA cert is only needed when the server's certificate comes from a private authority.";
            clearCerts.Visibility = applied.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshSsh()
        {
            bool on = overSsh.IsChecked == true;
            sshGroup.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            sshKeyButton.Content = sshKeyFile.Length > 0 ? "Key ✓" : "Private key…";
            ToolTipService.SetToolTip(sshKeyButton, sshKeyFile.Length > 0 ? sshKeyFile : "Sign in with a private key file instead of a password");
            sshKeyClear.Visibility = sshKeyFile.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)((Grid)sshSecretFormRow).Children[0]).Text = sshKeyFile.Length > 0 ? "Passphrase" : "Password";
            string recorded = Draft().SshHostKey;
            hostKeyNote.Text = recorded.Length > 0 ? "Host key " + recorded : "The server's host key is recorded on the first connection.";
            forgetHostKey.Visibility = recorded.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void ClearErrors() { foreach (var clear in new[] { hostError, userError, passwordError, databaseError, certError, sshHostError, sshUserError, sshSecretError }) clear(null); }

        // Any edit makes an earlier check out of date.
        void Changed()
        {
            if (filling) return;
            last = null; headline.Visibility = advice.Visibility = Visibility.Collapsed; fixes.Children.Clear(); ClearErrors();
            RefreshPreview(); RefreshSsh(); RenderSteps(ConnectionCheck.Plan(Draft()));
        }

        bool Validate()
        {
            ClearErrors(); bool ok = true;
            void Need(bool valid, Action<string?> error, string message, Control focus) { if (valid) return; error(message); if (ok) focus.Focus(FocusState.Programmatic); ok = false; }
            Need(host.Text.Trim().Length > 0, hostError, "Enter the host", host);
            Need(PortOf(port, 0) > 0, hostError, "Port is a whole number from 1 to 65535", port);
            Need(user.Text.Trim().Length > 0, userError, "Enter the user to sign in as", user);
            Need(database.Text.Trim().Length > 0, databaseError, "Enter the database name", database);
            Need(certFile.Length == 0 == (keyFile.Length == 0), certError, "A client cert and its key go together: apply both or neither", keyFile.Length == 0 ? keyButton : certButton);
            if (overSsh.IsChecked == true)
            {
                Need(sshHost.Text.Trim().Length > 0, sshHostError, "Enter the SSH host", sshHost);
                Need(PortOf(sshPort, 0) > 0, sshHostError, "Port is a whole number from 1 to 65535", sshPort);
                Need(sshUser.Text.Trim().Length > 0, sshUserError, "Enter the SSH user", sshUser);
                Need(sshKeyFile.Length > 0 || sshPassword.Password.Length > 0 || (!sshSecretChanged && p.SshSecret.Length > 0), sshSecretError, "Enter the SSH password, or choose a private key", sshPassword);
            }
            return ok;
        }

        void SetBusy(bool busy)
        {
            form.Opacity = busy ? .6 : 1; form.IsHitTestVisible = !busy;
            saveButton.IsEnabled = testButton.IsEnabled = connectButton.IsEnabled = overSsh.IsEnabled = !busy;
        }

        void ShowResult(CheckResult result)
        {
            last = result; RenderSteps(result.Steps);
            headline.Text = result.Headline; headline.Visibility = Visibility.Visible;
            if (!HighContrast) headline.Foreground = new SolidColorBrush(ToneColor(result.Ok ? Tone.Good : Tone.Warn, headline.ActualTheme == ElementTheme.Dark));
            advice.Text = result.Advice; advice.Visibility = result.Advice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            fixes.Children.Clear();
            if (result.Ok) return;
            var failed = result.Steps.FirstOrDefault(s => s.State == CheckState.Failed);
            // The fix sits beside the failure and acts on the same control the form uses.
            switch (result.Fix)
            {
                case "ca":
                    fixes.Children.Add(Button("CA cert…", async (_, _) => await ChooseCa()));
                    if (TlsModes[tls.SelectedIndex] is "VerifyFull" or "VerifyCA") fixes.Children.Add(Button("Set Require", (_, _) => tls.SelectedIndex = Array.IndexOf(TlsModes, "Require")));
                    break;
                case "tls": tls.Focus(FocusState.Programmatic); break;
                case "cert": certError(failed?.Detail); (certFile.Length == 0 ? certButton : keyButton).Focus(FocusState.Programmatic); break;
                case "password": passwordError(failed?.Detail); password.Focus(FocusState.Programmatic); break;
                case "database": databaseError(failed?.Detail); database.Focus(FocusState.Programmatic); break;
                case "host": hostError(failed?.Detail); host.Focus(FocusState.Programmatic); break;
                case "port": hostError(failed?.Detail); port.Focus(FocusState.Programmatic); break;
                case "ssh":
                    if (result.Raw.Contains(nameof(SshHostKeyChangedException))) fixes.Children.Add(Button("Forget host key", (_, _) => { hostKey = ""; Changed(); }));
                    else sshHost.Focus(FocusState.Programmatic);
                    break;
            }
            fixes.Children.Add(Button("Copy details", (_, _) =>
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(result.Headline + "\n" + string.Join("\n", result.Steps.Select(s => $"{s.State}: {s.Title}" + (s.Detail.Length > 0 ? " · " + s.Detail : ""))) + "\n\n" + result.Raw);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            }));
        }

        async Task<bool> RunCheck()
        {
            if (checking != null || !Validate()) return false;
            Profile candidate;
            try { candidate = Value(); } catch (Exception e) { headline.Text = e.Message; headline.Visibility = Visibility.Visible; return false; }
            checking = new(); SetBusy(true); headline.Visibility = advice.Visibility = Visibility.Collapsed; fixes.Children.Clear();
            stackedCheck?.StartBringIntoView();
            try
            {
                var result = await ConnectionCheck.Run(candidate, password.Password, RenderSteps, checking.Token);
                // Trust on first use: a key seen now is kept with the connection when it is saved.
                if (result.HostKey.Length > 0 && candidate.SshHostKey.Length == 0) { hostKey = result.HostKey; hostKeyHost = candidate.SshHost; hostKeyPort = candidate.SshPort; RefreshSsh(); }
                ShowResult(result);
                return result.Ok;
            }
            catch (OperationCanceledException) { RenderSteps(ConnectionCheck.Plan(candidate)); return false; }
            catch (Exception e) { RenderSteps(ConnectionCheck.Plan(candidate)); headline.Text = e.Message; headline.Visibility = Visibility.Visible; return false; }
            finally
            {
                checking.Dispose(); checking = null; SetBusy(false);
                // After the focus a failure moves to its field has settled, so the result is what stays in view.
                if (stackedCheck is { } check) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => check.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 1 }));
            }
        }

        async Task ChooseCa() { if (await PickFile(".pem", ".crt", ".cer") is { } path) { caFile = path; RefreshCerts(); Changed(); } }

        // ---- Wiring ------------------------------------------------------------------------------------
        saveButton = Button(isNew ? "Save" : "Save changes", (_, _) => { if (!Validate()) return; outcome = EditorOutcome.Save; dialog.Hide(); });
        testButton = Button("Test", async (_, _) => await RunCheck());
        connectButton = Button("Connect", async (_, _) => { if (await RunCheck()) { outcome = EditorOutcome.Connect; dialog.Hide(); } }, true);
        ToolTipService.SetToolTip(saveButton, "Save without connecting"); ToolTipService.SetToolTip(testButton, "Check the connection without saving"); ToolTipService.SetToolTip(connectButton, "Check, save and open this connection");

        for (int i = 0; i < artTiles.Length; i++)
        {
            int index = i; artTiles[i].IsChecked = i == art;
            artTiles[i].Click += (_, _) => { art = index; for (int t = 0; t < artTiles.Length; t++) artTiles[t].IsChecked = t == index; Changed(); };
        }
        foreach (var box in new[] { name, host, user, database, sshHost, sshUser }) box.TextChanged += (_, _) => Changed();
        foreach (var box in new[] { port, sshPort }) box.ValueChanged += (_, _) => Changed();
        foreach (var combo in new[] { tag, safety, store }) combo.SelectionChanged += (_, _) => Changed();
        password.PasswordChanged += (_, _) => { passwordChanged = true; Changed(); };
        sshPassword.PasswordChanged += (_, _) => { sshSecretChanged = true; Changed(); };
        tls.SelectionChanged += (_, _) => { if (!filling) tlsChosen = true; Changed(); };
        // Until a TLS mode is chosen by hand, a new connection follows its host: off for this computer, verified elsewhere.
        host.TextChanged += (_, _) => { if (isNew && !tlsChosen && !filling) { filling = true; try { tls.SelectedIndex = Array.IndexOf(TlsModes, ConnectionProfiles.DefaultTls(host.Text)); } finally { filling = false; } Changed(); } };
        overSsh.Click += (_, _) => { Changed(); if (overSsh.IsChecked == true) sshHost.Focus(FocusState.Programmatic); };
        keyButton.Click += async (_, _) => { if (await PickFile(".key", ".pem") is { } path) { keyFile = path; RefreshCerts(); Changed(); } };
        certButton.Click += async (_, _) => { if (await PickFile(".crt", ".pem", ".cer") is { } path) { certFile = path; RefreshCerts(); Changed(); } };
        caButton.Click += async (_, _) => await ChooseCa();
        clearCerts.Click += (_, _) => { keyFile = certFile = caFile = ""; RefreshCerts(); Changed(); };
        sshKeyButton.Click += async (_, _) => { if (await PickFile(".pem", ".key") is { } path) { sshKeyFile = path; Changed(); } };
        sshKeyClear.Click += (_, _) => { sshKeyFile = ""; Changed(); };
        forgetHostKey.Click += (_, _) => { hostKey = ""; Changed(); };
        // A paste is read at once. Typed text is read when the box is left or Enter is pressed, so a half-typed
        // URL is never taken for a whole one.
        int urlLength = 0;
        void ReadUrl()
        {
            if (filling || url.Text.Trim().Length == 0) return;
            var parsed = ConnectionUrl.Parse(url.Text);
            urlNote.Visibility = Visibility.Visible;
            if (parsed == null) { urlNote.Text = "That doesn't read as a Postgres URL or connection string."; return; }
            filling = true;
            try
            {
                if (parsed.Host != null) host.Text = parsed.Host;
                port.Value = parsed.Port ?? 5432;
                if (parsed.Database != null) database.Text = parsed.Database;
                if (parsed.Username != null) user.Text = parsed.Username;
                if (parsed.Password != null) { password.Password = parsed.Password; passwordChanged = true; }
                if (parsed.Ssl != null) { tls.SelectedIndex = Array.IndexOf(TlsModes, parsed.Ssl); tlsChosen = true; }
                else if (!tlsChosen) tls.SelectedIndex = Array.IndexOf(TlsModes, ConnectionProfiles.DefaultTls(host.Text));
                if (parsed.RootCertificate != null) caFile = parsed.RootCertificate;
                if (parsed.ClientCertificate != null) certFile = parsed.ClientCertificate;
                if (parsed.ClientKey != null) keyFile = parsed.ClientKey;
                // The pasted text can hold the password, so it does not stay on screen.
                url.Text = "";
            }
            finally { filling = false; }
            urlNote.Text = "Filled in " + string.Join(", ", parsed.Parts()) + (parsed.Ssl == null ? ". TLS mode was not in the text" + (tlsChosen ? "." : ", so it follows the host.") : ".");
            RefreshCerts(); Changed();
        }
        url.TextChanged += (_, _) => { bool pasted = url.Text.Length - urlLength > 1; urlLength = url.Text.Length; if (pasted) ReadUrl(); };
        url.LostFocus += (_, _) => ReadUrl();
        url.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; ReadUrl(); } };

        // ---- Layout ------------------------------------------------------------------------------------
        bool wide = root.ActualWidth >= 940;
        // The dialog is capped by the window, so the two scrollers take what is left after its title, padding and footer.
        double height = Math.Clamp(root.ActualHeight - 350, 200, 640);
        var side = Themed(new Border { Child = panel, Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) }, (s, dark) =>
        {
            if (HighContrast) { s.ClearValue(Border.BackgroundProperty); s.BorderBrush = SystemText(); return; }
            s.Background = new SolidColorBrush(Brand.Fade(dark ? Brand.Silver : Brand.Line, dark ? (byte)16 : (byte)18)); s.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        });
        var body = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        if (wide)
        {
            body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(248) });
            form.Margin = new Thickness(0, 0, 12, 0);
            body.Children.Add(new ScrollViewer { Content = form, MaxHeight = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var sideScroll = new ScrollViewer { Content = side, MaxHeight = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(sideScroll, 1); body.Children.Add(sideScroll);
        }
        else
        {
            // A narrow window stacks the check under the form inside one scroller.
            var stacked = Stack(form, side); stacked.Margin = new Thickness(0, 0, 12, 0); stackedCheck = side;
            body.Children.Add(new ScrollViewer { Content = stacked, MaxHeight = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }
        var footer = CommandRow([overSsh], [cancelButton, saveButton, testButton, connectButton]);
        var layout = new Grid { RowSpacing = 14, Width = wide ? 820 : Math.Clamp(root.ActualWidth - 110, 360, 560) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        if (note.Length > 0) form.Children.Insert(0, Muted(note, 12));
        layout.Children.Add(body); Grid.SetRow(footer, 1); layout.Children.Add(footer);

        RefreshCerts(); Changed();
        dialog = StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = isNew ? "New connection" : "Edit " + p.Name, Content = layout, RequestedTheme = root.RequestedTheme });
        dialog.Resources["ContentDialogMaxWidth"] = 900d; dialog.Resources["ContentDialogMaxHeight"] = 900d;
        dialog.Opened += (_, _) => (isNew ? (Control)url : name).Focus(FocusState.Programmatic);
        dialogOpen = true;
        try { await dialog.ShowAsync(); } finally { dialogOpen = false; checking?.Cancel(); }
        if (outcome == EditorOutcome.Cancel) return false;

        Profile updated;
        try { updated = Value(); }
        catch (Exception e) { feedback.Title = "Could not save connection"; feedback.Message = e.Message; feedback.Severity = InfoBarSeverity.Error; feedback.IsOpen = true; return false; }
        if (!SaveProfile(updated)) return false;
        if (passwordChanged || password.Password.Length > 0 || passwords.ContainsKey(p.Id)) passwords[updated.Id] = password.Password;
        requestPassword.Remove(updated.Id); connectionErrors.Remove(updated.Id);
        bool changedActive = db?.Profile.Id == updated.Id && !SavedSettingsAreActive(updated);
        if (!changedActive && db?.Profile.Id == updated.Id) { db.Profile.Name = updated.Name; db.Profile.Identity = updated.Identity; db.Profile.Production = updated.Production; db.Profile.Tag = updated.Tag; UpdateConnectionIdentity(db.Profile); }
        RefreshConnectionChrome();
        string saved = Store.IsUnreadable(Store.ConnectionsFile) ? "Kept for this session only (connections.json is left unchanged)" : "Saved";
        status.Text = changedActive ? saved + " · reconnect to apply; the current session is unchanged" : saved + " · ready to reuse";
        if (outcome == EditorOutcome.Connect) await ConnectProfile(updated, knownPassword: password.Password);
        else if (isNew) Navigate("Connections");
        return true;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DbCowboy;

public sealed partial class MainWindow
{
    readonly StackPanel connectionCards=new(){Spacing=12};
    readonly TextBox connectionSearch=new(){PlaceholderText="Find by name, host, database or user…",HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly TextBlock connectionCount=Text("",12);
    readonly Button disconnectSession=new(){Content="Disconnect"};
    readonly Dictionary<Guid,string> connectionErrors=[];
    readonly HashSet<Guid> requestPassword=[];
    Guid? connectingProfile;
    string? activeSessionKey;
    string lastWorkspacePage="Query";
    bool refreshingProfiles;
    Action? refreshTableChrome, refreshVectorChrome, refreshActivityChrome;

    /// <summary>Connections: every saved profile as a card, with the live session marked and one clear way in.</summary>
    FrameworkElement BuildConnectionLibrary()
    {
        connectionSearch.TextChanged += (_, _) => RenderConnectionLibrary();
        connectionSearch.HorizontalAlignment = HorizontalAlignment.Left; connectionSearch.MinWidth = 320; connectionSearch.MaxWidth = 460;
        AutomationProperties.SetName(connectionSearch, "Search saved Postgres connections");
        BrandControl(connectionSearch);

        var add = ToolButton(Symbol.Add, "New connection", NewConnectionClick, "Save the details of another Postgres database", accent: true);
        var header = PageHeader("Connections", "Saved on this computer. Open one to start working, switch databases, or keep a few favorites close.", add);

        var count = Muted("", 13);
        count.VerticalAlignment = VerticalAlignment.Center;
        // connectionCount is written by the renderer; the muted label mirrors it.
        connectionCount.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => count.Text = connectionCount.Text);
        var tools = new Grid { ColumnSpacing = 12 };
        tools.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tools.Children.Add(connectionSearch);
        Grid.SetColumn(count, 1); tools.Children.Add(count);

        var scroll = new ScrollViewer { Content = connectionCards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 2, 14, 8) };
        var page = new Grid { RowSpacing = 14 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) page.RowDefinitions.Add(new() { Height = height });
        page.Children.Add(header);
        Grid.SetRow(tools, 1); page.Children.Add(tools);
        Grid.SetRow(scroll, 2); page.Children.Add(scroll);
        return page;
    }

    /// <summary>Called whenever connection state may have changed: the sidebar block, Home, Extensions and the pages follow.</summary>
    void RefreshConnectionChrome()
    {
        if (connect == null) return;
        UpdateTitleConnection();
        ExtensionsConnectionChanged(); refreshHome?.Invoke(); refreshTableChrome?.Invoke(); refreshVectorChrome?.Invoke(); refreshActivityChrome?.Invoke(); refreshSqlChrome?.Invoke();
        var selected = connections.SelectedItem as Profile;
        bool busy = operation != null || connectingProfile != null;
        ToolTipService.SetToolTip(connections, selected == null ? "Choose a saved connection" : $"{selected.Username}@{selected.Host}:{selected.Port}/{selected.Database}");

        // The primary action names what will happen to the selected profile.
        string action = connectingProfile != null ? "Connecting…"
            : selected == null ? "New connection"
            : SavedSettingsAreActive(selected) ? "Open"
            : db != null && selected.Id == db.Profile.Id ? "Reconnect"
            : db != null ? "Switch" : "Connect";
        connect.Content = action;
        AutomationProperties.SetName(connect, selected == null ? "Create Postgres connection" : (SavedSettingsAreActive(selected) ? "Open active workspace " : "Connect to ") + selected.Name);
        ToolTipService.SetToolTip(connect, selected == null ? "Save the details of a Postgres database" : action + " · " + selected.Name);
        connect.IsEnabled = connections.IsEnabled = !busy;
        disconnectSession.IsEnabled = db != null && !busy;
        // Disconnect only means something while a session is live.
        disconnectSession.Visibility = db != null ? Visibility.Visible : Visibility.Collapsed;
        RenderConnectionState();
        RenderConnectionLibrary();
    }

    void RenderConnectionLibrary()
    {
        connectionCards.Children.Clear();
        var visible = ConnectionProfiles.Ordered(profiles, connectionSearch.Text).ToList();
        int favorites = profiles.Count(p => p.Favorite);
        connectionCount.Text = $"{profiles.Count} saved · {favorites} " + (favorites == 1 ? "favorite" : "favorites");
        if (profiles.Count == 0)
        {
            connectionCards.Children.Add(EmptyState("01-got-you", "Your first connection starts here", "Save a database's details once, then open it whenever you need it. Passwords stay on this Windows account.", Button("New connection", NewConnectionClick, true)));
            return;
        }
        if (visible.Count == 0)
        {
            connectionCards.Children.Add(EmptyState("02-wait-what", "No connection matches", "Try part of a name, host, database or username.", Button("Clear search", (_, _) => connectionSearch.Text = "")));
            return;
        }
        connectionCards.Children.Add(Fluent.ArrowKeys(Fluent.Stagger(Flow(420, false, visible.Select(ConnectionCard).ToArray()))));
    }

    /// <summary>Identity art when the profile has one; otherwise its initial on a quiet tile.</summary>
    static FrameworkElement ConnectionIdentityArt(Profile profile, double size)
    {
        int identity = Array.IndexOf(IdentityNames, profile.Identity);
        if (identity > 0)
        {
            var art = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, Source = Asset(IdentityFiles[identity]), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(art, profile.Identity);
            return art;
        }
        var initial = Display(profile.Name.Trim().Length > 0 ? profile.Name.Trim()[..1].ToUpperInvariant() : "?", size * .42);
        initial.HorizontalAlignment = HorizontalAlignment.Center; initial.VerticalAlignment = VerticalAlignment.Center;
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(12), Child = initial, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAccessibilityView(tile, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return Themed(tile, (t, dark) =>
        {
            if (HighContrast) { t.ClearValue(Border.BackgroundProperty); return; }
            t.Background = new SolidColorBrush(Brand.Fade(ToneColor(Tone.Good, dark), dark ? (byte)46 : (byte)30));
            initial.Foreground = new SolidColorBrush(ToneColor(Tone.Good, dark));
        });
    }

    FrameworkElement ConnectionCard(Profile p)
    {
        bool active = db?.Profile.Id == p.Id, busy = operation != null || connectingProfile != null;
        bool currentSettings = SavedSettingsAreActive(p);

        var pills = Row();
        pills.Spacing = 6;
        if (connectingProfile == p.Id) pills.Children.Add(Pill("Connecting…", Tone.Accent));
        else if (active) pills.Children.Add(Pill("Active session", Tone.Good));
        if (p.Production) pills.Children.Add(Pill("Production", Tone.Warn));
        if (p.Favorite) pills.Children.Add(Pill("Favorite", Tone.Accent));
        pills.Children.Add(Pill(p.ReadOnly ? "Read-only by default" : "Writes need confirmation"));

        var name = Display(p.Name, 19);
        name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(name, p.Name);
        var endpoint = Text($"{p.Host}:{p.Port} / {p.Database}", 13);
        endpoint.TextWrapping = TextWrapping.NoWrap; endpoint.TextTrimming = TextTrimming.CharacterEllipsis; endpoint.IsTextSelectionEnabled = true;
        string credential = p.Secret.Length > 0 ? "password saved for this Windows account" : passwords.ContainsKey(p.Id) ? "password held for this app session" : "password asked when connecting";
        var facts = Muted($"{p.Username} · TLS {p.Ssl} · {credential}", 12);

        var info = new StackPanel { Spacing = 3 };
        info.Children.Add(name); info.Children.Add(endpoint); info.Children.Add(facts);
        var top = new Grid { ColumnSpacing = 14 };
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(ConnectionIdentityArt(p, 56));
        Grid.SetColumn(info, 1); top.Children.Add(info);

        var open = Button(currentSettings ? "Open workspace" : active ? "Reconnect to apply" : db == null ? "Connect" : "Switch here", async (_, _) => await ConnectProfile(p), true);
        open.IsEnabled = !busy;
        AutomationProperties.SetName(open, $"{(currentSettings ? "Open workspace" : "Connect")} {p.Name}");
        var edit = Button("Edit", async (_, _) => await EditConnection(p));
        edit.IsEnabled = !busy;
        AutomationProperties.SetName(edit, "Edit " + p.Name);
        var favorite = IconButton(new FontIcon { Glyph = p.Favorite ? "" : "" }, (p.Favorite ? "Unfavorite " : "Favorite ") + p.Name, (_, _) => SaveProfile(p with { Favorite = !p.Favorite }, false));
        favorite.IsEnabled = !busy;
        Button more = null!;
        more = IconButton(Symbol.More, "More actions for " + p.Name, (_, _) => ConnectionMenu(more, p));
        more.IsEnabled = !busy;

        var last = Muted(p.LastConnected is { } at ? "Last opened " + at.ToLocalTime().ToString("MMM d, h:mm tt") : "Not opened yet", 12);
        last.VerticalAlignment = VerticalAlignment.Center; last.HorizontalAlignment = HorizontalAlignment.Right; last.TextWrapping = TextWrapping.NoWrap;
        var actions = new Grid { ColumnSpacing = 10 };
        actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        actions.Children.Add(Row(open, edit, favorite, more));
        Grid.SetColumn(last, 1); actions.Children.Add(last);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(ScrollTools(pills)); body.Children.Add(top);
        if (active && !currentSettings) body.Children.Add(Muted("Saved settings changed. Reconnect to apply them; the live session still uses the previous settings.", 12));
        if (connectionErrors.TryGetValue(p.Id, out var error))
            body.Children.Add(Themed(Text(error, 12), (label, dark) => { if (!HighContrast) label.Foreground = new SolidColorBrush(ToneColor(Tone.Warn, dark)); }));
        body.Children.Add(actions);

        var card = Surface(body, 18);
        // The live session's card carries the teal outline.
        if (active) Themed(card, (c, dark) => { if (!HighContrast) { c.BorderBrush = new SolidColorBrush(ToneColor(Tone.Good, dark)); c.BorderThickness = new Thickness(2); } });
        return Fluent.Lift(card);
    }

    void ConnectionMenu(FrameworkElement anchor,Profile profile)
    {
        var menu=Fluent.Acrylic(new MenuFlyout());var duplicate=new MenuFlyoutItem{Text="Duplicate connection…"};duplicate.Click+=async(_,_)=>await EditConnection(ConnectionProfiles.Duplicate(profile),true);menu.Items.Add(duplicate);
        if(db?.Profile.Id==profile.Id){var reconnect=new MenuFlyoutItem{Text="Reconnect with saved settings"};reconnect.Click+=async(_,_)=>await ConnectProfile(profile,true);menu.Items.Add(reconnect);var disconnect=new MenuFlyoutItem{Text="Disconnect active session"};disconnect.Click+=DisconnectClick;menu.Items.Add(disconnect);}
        menu.Items.Add(new MenuFlyoutSeparator());var remove=new MenuFlyoutItem{Text="Remove saved connection…"};remove.Click+=async(_,_)=>
        {
            if(operation!=null||connectingProfile!=null)return;
            if(!await Confirm("Remove saved connection?",profile.Name+"\n\nRemoves this saved profile from DB Cowboy. Database tables and saved SQL workspaces are unaffected."+(db?.Profile.Id==profile.Id?" The active session will remain open until you disconnect.":""),"Remove profile"))return;
            try{WriteProfiles(profiles.Where(p=>p.Id!=profile.Id).ToList());}
            catch(Exception ex){feedback.Title="Could not remove connection";feedback.Message=ex.Message;feedback.Severity=InfoBarSeverity.Error;feedback.IsOpen=true;status.Text="Saved profile was not removed · check local storage";return;}
            passwords.Remove(profile.Id);requestPassword.Remove(profile.Id);connectionErrors.Remove(profile.Id);RefreshProfiles();
            status.Text=Store.IsUnreadable(Store.ConnectionsFile)?"Profile removed for this session only · connections.json is left unchanged":"Saved profile removed · database untouched";
        };menu.Items.Add(remove);menu.ShowAt(anchor);
    }
    /// <summary>
    /// Replaces the saved connections. When connections.json could not be read at startup the change applies to
    /// this session only and the file is left as it was.
    /// </summary>
    void WriteProfiles(List<Profile> next)
    {
        if(!Store.IsUnreadable(Store.ConnectionsFile))Store.Write(Store.ConnectionsFile,next);
        profiles=next;
    }
    bool SaveProfile(Profile profile,bool select=true)
    {
        try{WriteProfiles(ConnectionProfiles.Upsert(profiles,profile));feedback.IsOpen=false;RefreshProfiles(select?profile:null);return true;}
        catch(Exception ex){feedback.Title="Could not save connection";feedback.Message=ex.Message;feedback.Severity=InfoBarSeverity.Error;feedback.IsOpen=true;status.Text="Connection was not saved · check local storage";return false;}
    }
    void RememberSelectedConnection()
    {
        if(refreshingProfiles)return;
        workspace.LastSelectedConnection=(connections.SelectedItem as Profile)?.Id;SaveWorkspaceSoon();RefreshConnectionChrome();
    }
    async Task<(string Password,bool? Remember)?> ConnectionPassword(Profile profile)
    {
        if(!requestPassword.Contains(profile.Id))
        {
            if(passwords.TryGetValue(profile.Id,out var cached))return(cached,null);
            try{if(profile.Secret.Length>0)return(Store.Unprotect(profile.Secret),null);}catch{requestPassword.Add(profile.Id);}
        }
        if(dialogOpen)return null;
        var password=new PasswordBox{Header="Password for "+profile.Username,PasswordRevealMode=PasswordRevealMode.Peek};
        var remember=new CheckBox{Content="Remember on this Windows account",IsChecked=profile.Secret.Length>0};
        var dialog=StyleDialog(new ContentDialog { XamlRoot = root.XamlRoot,Title="Connect to "+profile.Name,Content=Stack(Text($"{profile.Host}:{profile.Port} / {profile.Database}",13),Text("Your connection details are already saved. Only the password is needed; leave it empty for a server that does not require one.",12),password,remember),PrimaryButtonText="Connect",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Primary,RequestedTheme=root.RequestedTheme});
        dialog.Opened+=(_,_)=>password.Focus(FocusState.Programmatic);
        dialogOpen=true;try{return await dialog.ShowAsync()==ContentDialogResult.Primary?(password.Password,remember.IsChecked==true):null;}finally{dialogOpen=false;}
    }
}

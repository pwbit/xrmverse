namespace Xrmd.Metadata;

/// <summary>
/// The seeded tables of the model-driven app shell (plan 3.5 UI-1a): <c>appmodulecomponent</c>, the <c>appmoduleroles</c>
/// intersect of <c>appmoduleroles_association</c>, and the user-owned <c>userquery</c> (personal views). Names and values of
/// Learn's AppModule, AppModuleComponent and UserQuery table references.
/// </summary>
public static class AppModuleSchema
{
    /// <summary>The table of an app's components (organization-owned, written by the solution import only).</summary>
    public const string Component = "appmodulecomponent";

    /// <summary>The intersect table of <see cref="RolesRelationship"/> (<c>appmoduleid</c>, <c>roleid</c>).</summary>
    public const string RolesIntersect = "appmoduleroles";

    /// <summary>The many-to-many relationship between <c>appmodule</c> and <c>role</c>.</summary>
    public const string RolesRelationship = "appmoduleroles_association";

    /// <summary>The relationship from <c>appmodulecomponent.appmoduleidunique</c> to <c>appmodule</c>.</summary>
    public const string ComponentToApp = "appmodule_appmodulecomponent";

    /// <summary>The personal view table (user-owned).</summary>
    public const string UserQuery = "userquery";

    /// <summary>The object type code of <c>userquery</c>, which a view selector names as <c>viewType</c>.</summary>
    public const int UserQueryTypeCode = 4230;

    /// <summary>
    /// The user-owned <c>userentityuisettings</c> (#119): per user and table, the last form used (<c>lastviewedformxml</c>,
    /// <c>&lt;MRUForm&gt;&lt;Form Type="Main" Id="{guid}" /&gt;&lt;/MRUForm&gt;</c>), as Dataverse stores it.
    /// </summary>
    public const string UserEntityUiSettings = "userentityuisettings";

    /// <summary>
    /// <c>organization.isautosaveenabled</c> (#117): whether the model-driven forms save a changed record by themselves. On by default,
    /// as in Dataverse.
    /// </summary>
    public const string AutoSaveEnabled = "isautosaveenabled";

    /// <summary>
    /// The organization-owned definitions of the app settings (#129), seeded with the rows of <see cref="AsyncOnLoad"/> and
    /// <see cref="AsyncOnSave"/>. Dataverse ships the table in a solution with a per-environment type code; xrmverse assigns 9966.
    /// </summary>
    public const string SettingDefinition = "settingdefinition";

    /// <summary>The organization-owned values of the app settings (#129): one row per app and definition (type code 9965, ours).</summary>
    public const string AppSetting = "appsetting";

    /// <summary>The relationship from <c>appsetting.parentappmoduleid</c> to <c>appmodule</c> (Delete Cascade; the name is ours).</summary>
    public const string AppSettingToApp = "appmodule_appsetting_parentappmoduleid";

    /// <summary>The relationship from <c>appsetting.settingdefinitionid</c> to <c>settingdefinition</c> (Delete Restrict; the name is ours).</summary>
    public const string AppSettingToDefinition = "settingdefinition_appsetting";

    /// <summary>The setting that makes the forms of an app wait for the promises their OnLoad handlers return.</summary>
    public const string AsyncOnLoad = "AsyncOnLoad";

    /// <summary>The setting that makes the forms of an app wait for the promises their OnSave handlers return.</summary>
    public const string AsyncOnSave = "AsyncOnSave";

    /// <summary>The well-known id of the <see cref="AsyncOnLoad"/> definition (observed on a Dataverse environment).</summary>
    public static readonly Guid AsyncOnLoadId = new("fe39737f-4953-4a92-9943-56a697b82c7b");

    /// <summary>The well-known id of the <see cref="AsyncOnSave"/> definition (observed on a Dataverse environment).</summary>
    public static readonly Guid AsyncOnSaveId = new("aaf2a566-153a-4384-9acb-7d762a411c6d");

    /// <summary>The setting that makes the timeline group its records by date.</summary>
    public const string TimelineGroupByDate = "xrmverse_TimelineGroupByDate";

    /// <summary>The setting that makes the timeline remember a user's filters.</summary>
    public const string TimelineRememberFilters = "xrmverse_TimelineRememberFilters";

    /// <summary>The id of the <see cref="TimelineGroupByDate"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid TimelineGroupByDateId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e01");

    /// <summary>The id of the <see cref="TimelineRememberFilters"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid TimelineRememberFiltersId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e02");

    /// <summary>The setting that turns on the posts of the timeline (plan 3.5 UI-1g, posts).</summary>
    public const string TimelinePosts = "xrmverse_TimelinePosts";

    /// <summary>The id of the <see cref="TimelinePosts"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid TimelinePostsId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e03");

    /// <summary>The setting that names the renderer of the apps (UI-2a): <c>classic</c> or <c>modern</c>.</summary>
    public const string Renderer = "xrmverse_Renderer";

    /// <summary>The id of the <see cref="Renderer"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid RendererId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e10");

    /// <summary>
    /// The setting that lists the web resource functions run once when an app opens (X.10): a JSON array of
    /// <c>{ "library": string, "function": string }</c>; empty by default.
    /// </summary>
    public const string AppOnLoad = "xrmverse_AppOnLoad";

    /// <summary>The id of the <see cref="AppOnLoad"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid AppOnLoadId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e20");

    /// <summary>The default value of <see cref="AppOnLoad"/>: empty.</summary>
    public const string AppOnLoadDefault = "";

    /// <summary>
    /// The environment setting that says who may send email (plan 4.1b, an xrmverse extension; Dataverse has no such switch):
    /// <see cref="EmailSendFromUserOrQueue"/> (the default, as Dataverse) or <see cref="EmailSendFromQueueOnly"/>, where SendEmail with
    /// <c>IssueSend</c> true sends only from a queue.
    /// </summary>
    public const string EmailSendFrom = "xrmverse_EmailSendFrom";

    /// <summary>The id of the <see cref="EmailSendFrom"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid EmailSendFromId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e21");

    /// <summary>The values of <see cref="EmailSendFrom"/>: a user or a queue sends (the default), or only a queue does.</summary>
    public const string EmailSendFromUserOrQueue = "userOrQueue", EmailSendFromQueueOnly = "queueOnly";

    /// <summary>The default value of <see cref="Renderer"/>.</summary>
    public const string RendererDefault = "classic";

    /// <summary>
    /// The setting that says how model-driven forms open a related record (a lookup's value, a subgrid's row or New):
    /// <c>sidepane</c> opens its main form in a side dialog two thirds as wide as the form (an xrmverse extension, the default),
    /// <c>page</c> navigates to it as Dataverse does.
    /// </summary>
    public const string LookupOpenMode = "xrmverse_LookupOpenMode";

    /// <summary>The id of the <see cref="LookupOpenMode"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid LookupOpenModeId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e30");

    /// <summary>The default value of <see cref="LookupOpenMode"/>.</summary>
    public const string LookupOpenModeDefault = "sidepane";

    /// <summary>The setting that holds the presentation (look) overrides of the apps (UI-2a); empty by default.</summary>
    public const string Presentation = "xrmverse_Presentation";

    /// <summary>The id of the <see cref="Presentation"/> definition (assigned by xrmverse).</summary>
    public static readonly Guid PresentationId = new("6f1c2a5e-8d4b-4e37-9a61-3b2f7c0d1e11");

    /// <summary>The default value of <see cref="Presentation"/>: empty.</summary>
    public const string PresentationDefault = "";

    /// <summary>
    /// The organization-owned environment values of the settings (UI-2a): at most one row per definition. The name follows Learn's
    /// customized app experiences article; the type code 9967 is assigned by xrmverse.
    /// </summary>
    public const string OrganizationSetting = "organizationsetting";

    /// <summary>The relationship from <c>organizationsetting.settingdefinitionid</c> to <c>settingdefinition</c> (Delete Restrict; the name is ours).</summary>
    public const string OrganizationSettingToDefinition = "settingdefinition_organizationsetting";

    /// <summary>The column of <c>usersettings</c> with the renderer a user chose; null follows the app (UI-2a).</summary>
    public const string UserRenderer = "xrmverse_renderer";

    /// <summary>The column of <c>usersettings</c> with the color scheme a user chose: 0 System, 1 Light, 2 Dark (UI-2a).</summary>
    public const string UserColorScheme = "xrmverse_colorscheme";

    /// <summary>The default <see cref="UserColorScheme"/>: System.</summary>
    public const int DefaultColorScheme = 0;

    /// <summary>The refusal (400) of a write of <see cref="Component"/> or <see cref="RolesIntersect"/>: a solution import writes them.</summary>
    public const string ImportOnly = "App modules are created by solution import in this version.";

    /// <summary>The column of <c>usersettings</c> that holds the rows per page of a list view, and its default.</summary>
    public const string PagingLimit = "paginglimit";

    /// <summary>The default <see cref="PagingLimit"/>, written to the settings of every user.</summary>
    public const int DefaultPagingLimit = 50;

    /// <summary>The <c>appmodulecomponent.componenttype</c> values the shell uses (any other value from a file is stored as is).</summary>
    public static class ComponentType
    {
        /// <summary>Entities.</summary>
        public const int Entity = 1;

        /// <summary>Views.</summary>
        public const int View = 26;

        /// <summary>Business process flows.</summary>
        public const int BusinessProcessFlow = 29;

        /// <summary>Charts.</summary>
        public const int Chart = 59;

        /// <summary>Forms.</summary>
        public const int Form = 60;

        /// <summary>The site map.</summary>
        public const int SiteMap = 62;
    }
}

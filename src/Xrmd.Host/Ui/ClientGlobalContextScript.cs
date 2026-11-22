namespace Xrmd.Host.Ui;

/// <summary>
/// <c>GET /ClientGlobalContext.js.aspx</c> (plan 3.5 UI-1c, task 2): the script an HTML web resource loads to get
/// <c>GetGlobalContext()</c>. It is our own short script, written from the plan: it asks the app shell that hosts the page (its
/// <c>parent</c>, or the <c>opener</c> of a window opened by <c>Xrm.Navigation.openWebResource</c>) for
/// <c>Xrm.Utility.getGlobalContext()</c>, and puts the shell's <c>Xrm</c> on <c>window.Xrm</c> when the page has none. A page
/// opened outside the shell gets an explicit error from <c>GetGlobalContext()</c>. Authenticated like a web resource (the bearer
/// header or the session cookie).
/// </summary>
internal static class ClientGlobalContextScript
{
    public const string Path = "/ClientGlobalContext.js.aspx";

    internal const string Source = """
        /* xrmverse: GetGlobalContext() for HTML web resources, from the app shell that hosts the page. */
        (function () {
          "use strict";
          function shellXrm() {
            var hosts = [window.parent !== window ? window.parent : null, window.opener];
            for (var i = 0; i < hosts.length; i++) {
              try {
                var xrm = hosts[i] && hosts[i].Xrm;
                if (xrm && xrm.Utility && typeof xrm.Utility.getGlobalContext === "function") return xrm;
              } catch (e) {
                /* another origin: not our shell */
              }
            }
            return null;
          }
          window.GetGlobalContext = function () {
            var xrm = shellXrm();
            if (!xrm) throw new Error("GetGlobalContext is available only in a page opened by the xrmverse app.");
            return xrm.Utility.getGlobalContext();
          };
          if (!window.Xrm) {
            var xrm = shellXrm();
            if (xrm) window.Xrm = xrm;
          }
        })();

        """;

    public static IEndpointRouteBuilder MapClientGlobalContextScript(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (HttpContext context) =>
        {
            var headers = context.Response.Headers;
            headers.CacheControl = "private, no-cache";
            headers.XContentTypeOptions = "nosniff";
            return Results.Text(Source, "application/javascript; charset=utf-8");
        }).RequireAuthorization();
        return app;
    }
}

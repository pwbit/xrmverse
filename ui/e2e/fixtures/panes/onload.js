var Xv = window.Xv || {};
Xv.Panes = Xv.Panes || {};

/** App OnLoad handler of the panes fixture: a console pane that cannot be closed, and a notification with an action. */
Xv.Panes.onAppLoad = function (executionContext) {
  var sidePanes = XrmVerse.App.sidePanes;
  return sidePanes
    .createPane({ paneId: 'console', title: 'Console', canClose: false, alwaysRender: true })
    .then(function (pane) {
      Xv.Panes.console = pane;
      return pane.navigate({ pageType: 'webresource', webresourceName: 'pn_/panes/console.html' });
    })
    .then(function () {
      return XrmVerse.App.addGlobalNotification({
        level: 4,
        message: 'Console ready',
        action: {
          actionLabel: 'Mark console',
          eventHandler: function () {
            Xv.Panes.console.badge = 3;
          },
        },
      });
    });
};

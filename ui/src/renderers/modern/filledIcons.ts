import { makeStyles } from '@fluentui/react-components';

/**
 * The modern look uses Fluent's filled icons: every bundled icon (shell/icons.ts) shows its filled variant. The class goes on
 * the modern look's FluentProvider (its theme scope), which Fluent also puts on the mount node of every portal below it, so
 * menus, dialogs, popovers and toasts show filled icons too; the classic look never carries it.
 */
export const useFilledIconStyles = makeStyles({
  root: {
    '& .fui-Icon-filled': { display: 'inline !important' },
    '& .fui-Icon-regular': { display: 'none !important' },
  },
});

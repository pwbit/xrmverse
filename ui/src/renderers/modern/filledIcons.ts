import { makeStyles } from '@fluentui/react-components';

/**
 * The modern look uses Fluent's filled icons: every bundled icon (shell/icons.ts) shows its filled variant. The class goes on
 * the modern look's FluentProvider (its theme scope), which Fluent also puts on the mount node of every portal below it, so
 * menus, dialogs, popovers and toasts show filled icons too; the classic look never carries it.
 *
 * Fluent's Nav draws a filled icon only on the selected item: elsewhere it paints it `transparent` and shows the regular one.
 * With the regular icon hidden, the navigation's icons give the filled one back the item's colour, so every entry keeps its
 * icon (the owner's choice, 2026-11-27); the selected item's brand colour still comes from Fluent's animation, which wins
 * over this declaration.
 */
const navIconColor = { color: 'inherit' };

export const useFilledIconStyles = makeStyles({
  root: {
    '& .fui-Icon-filled': { display: 'inline !important' },
    '& .fui-Icon-regular': { display: 'none !important' },
    '& .fui-NavItem__icon .fui-Icon-filled': navIconColor,
    '& .fui-NavCategoryItem__icon .fui-Icon-filled': navIconColor,
    '& .fui-AppItem__icon .fui-Icon-filled': navIconColor,
  },
});

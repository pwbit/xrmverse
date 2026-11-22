import { Menu, MenuButton, MenuItemRadio, MenuList, MenuPopover, MenuTrigger } from '@fluentui/react-components';
import type { ViewMode } from '../../parsers/presentationJson';
import { t } from '../../shell/i18n/t';
import { ShowAsIcon } from '../../shell/icons';

/**
 * The modes a view offers (grid, board, calendar, cards) as the "Show as" menu of the view's command bar; nothing when the
 * view has the grid only. The button names the current mode.
 */
export function ViewModeSwitcher({ modes, mode, onChange }: { modes: ViewMode[]; mode: ViewMode; onChange(mode: ViewMode): void }) {
  if (modes.length < 2) return null;
  return (
    <Menu checkedValues={{ mode: [mode] }} onCheckedValueChange={(_e, data) => data.checkedItems[0] && onChange(data.checkedItems[0] as ViewMode)}>
      <MenuTrigger disableButtonEnhancement>
        <MenuButton appearance="subtle" icon={<ShowAsIcon />} style={{ whiteSpace: 'nowrap', flexShrink: 0 }}>
          {t('apps.views.modes.button', { mode: t(`apps.views.modes.${mode}`) })}
        </MenuButton>
      </MenuTrigger>
      <MenuPopover>
        <MenuList aria-label={t('apps.views.modes.label')}>
          {modes.map((m) => (
            <MenuItemRadio key={m} name="mode" value={m}>
              {t(`apps.views.modes.${m}`)}
            </MenuItemRadio>
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

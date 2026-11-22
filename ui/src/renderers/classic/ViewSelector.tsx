import { Menu, MenuButton, MenuDivider, MenuItemRadio, MenuList, MenuPopover, MenuTrigger } from '@fluentui/react-components';
import type { ViewModel } from '../../model/view';
import { t } from '../../shell/i18n/t';

export interface ViewChoice {
  view: ViewModel;
  /** A personal view (`userquery`) rather than a public one (`savedquery`). */
  personal: boolean;
}

export interface ViewSelectorProps {
  /** Public views, sorted by name. */
  system: ViewModel[];
  /** Personal views, sorted by name. */
  personal: ViewModel[];
  selected: ViewChoice;
  onSelect: (choice: ViewChoice) => void;
  /** The button's class (the view's header shows the selector as its title). */
  className?: string;
}

const valueOf = (choice: ViewChoice) => `${choice.personal ? 'p' : 's'}:${choice.view.id}`;

/** The view menu: the public views, a separator, then the personal views. */
export function ViewSelector({ system, personal, selected, onSelect, className }: ViewSelectorProps) {
  const choices = [...system.map((view) => ({ view, personal: false })), ...personal.map((view) => ({ view, personal: true }))];
  const item = (choice: ViewChoice) => (
    <MenuItemRadio key={valueOf(choice)} name="view" value={valueOf(choice)}>
      {choice.view.name}
    </MenuItemRadio>
  );
  return (
    <Menu
      checkedValues={{ view: [valueOf(selected)] }}
      onCheckedValueChange={(_e, data) => {
        const choice = choices.find((c) => valueOf(c) === data.checkedItems[0]);
        if (choice && valueOf(choice) !== valueOf(selected)) onSelect(choice);
      }}
    >
      <MenuTrigger disableButtonEnhancement>
        <MenuButton className={className} appearance="subtle" size="large" aria-label={t('apps.views.selector', { name: selected.view.name })}>
          {selected.view.name}
        </MenuButton>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {choices.filter((c) => !c.personal).map(item)}
          {system.length > 0 && personal.length > 0 && <MenuDivider />}
          {choices.filter((c) => c.personal).map(item)}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

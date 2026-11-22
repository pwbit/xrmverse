import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Tooltip } from '@fluentui/react-components';
import type { EntityMeta } from '../../../../admin/tables/metadata';
import { t } from '../../../../shell/i18n/t';
import { ActivityIcon, NewIcon, RECORD_TYPE_ICONS } from '../../../../shell/icons';

export interface TimelineCreateMenuProps {
  /** The activity tables the user may create, in menu order. */
  types: EntityMeta[];
  onCreate: (table: string) => void;
  /** Offers Note as the first item (the user may create notes here); it opens the note entry. */
  onNote?: () => void;
  /** Offers Post after Note (posts are on and the user may create them); it opens the post entry. */
  onPost?: () => void;
}

/**
 * The header's + (spec §5.1): Note first (it opens the note entry), then Post (it opens the post entry), then one item per
 * activity type the user may create; a new activity opens its main form.
 */
export function TimelineCreateMenu({ types, onCreate, onNote, onPost }: TimelineCreateMenuProps) {
  if (types.length === 0 && !onNote && !onPost) return null;
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={t('record.timeline.create')} relationship="label">
          <Button appearance="subtle" icon={<NewIcon />} />
        </Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {onNote && (
            <MenuItem key="annotation" icon={<RECORD_TYPE_ICONS.annotation />} onClick={onNote}>
              {t('record.timeline.note')}
            </MenuItem>
          )}
          {onPost && (
            <MenuItem key="post" icon={<RECORD_TYPE_ICONS.post />} onClick={onPost}>
              {t('record.timeline.post')}
            </MenuItem>
          )}
          {types.map((e) => {
            const Icon = RECORD_TYPE_ICONS[e.logicalName] ?? ActivityIcon;
            return (
              <MenuItem key={e.logicalName} icon={<Icon />} onClick={() => onCreate(e.logicalName)}>
                {e.displayName}
              </MenuItem>
            );
          })}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}

import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger } from '@fluentui/react-components';
import { useState } from 'react';
import { useAuth } from '../shell/auth/AuthProvider';
import { t } from '../shell/i18n/t';
import { PersonalizationDialog } from './PersonalizationDialog';

/** The user's name as a menu (classic and modern headers share it): "Personalization" opens {@link PersonalizationDialog}. */
export function UserMenu({ buttonClassName, appearance = 'transparent' }: { buttonClassName?: string; appearance?: 'transparent' | 'subtle' }) {
  const { user } = useAuth();
  const [open, setOpen] = useState(false);
  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button appearance={appearance} className={buttonClassName} aria-label={user?.name ?? t('personalization.userMenu')}>
            {user?.name}
          </Button>
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem onClick={() => setOpen(true)}>{t('personalization.menu')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      {open && <PersonalizationDialog open onClose={() => setOpen(false)} />}
    </>
  );
}

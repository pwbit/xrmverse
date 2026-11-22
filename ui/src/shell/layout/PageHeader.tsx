import {
  Breadcrumb,
  BreadcrumbButton,
  BreadcrumbDivider,
  BreadcrumbItem,
  makeStyles,
  mergeClasses,
  Title2,
  Toolbar,
} from '@fluentui/react-components';
import { Fragment, type ReactNode } from 'react';
import { useHref, useLinkClickHandler } from 'react-router-dom';
import { t } from '../i18n/t';
import { useSurfaceStyles } from './surface';

export interface BreadcrumbEntry {
  label: string;
  to: string;
}

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', rowGap: '4px', marginBottom: '16px' },
  row: { display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: '16px', rowGap: '8px' },
  title: { margin: 0, flexGrow: 1, minWidth: 0, overflowWrap: 'anywhere' },
  actions: { display: 'flex', flexWrap: 'wrap', gap: '8px', marginTop: '8px' },
});

/** A breadcrumb entry that navigates inside the router (no page reload). */
function BreadcrumbLink({ entry }: { entry: BreadcrumbEntry }) {
  const href = useHref(entry.to);
  const onClick = useLinkClickHandler<HTMLAnchorElement>(entry.to);
  return (
    <BreadcrumbButton as="a" href={href} onClick={onClick}>
      {entry.label}
    </BreadcrumbButton>
  );
}

/**
 * The title block of a web admin page: an optional breadcrumb of the parent pages (the current page is the title), then the page title (an `h2` on
 * its own line), then the page's commands in a command bar outlined by shadow2, as on a model-driven form.
 */
export function PageHeader({ title, breadcrumb, actions }: { title: string; breadcrumb?: BreadcrumbEntry[]; actions?: ReactNode }) {
  const styles = useStyles();
  const surface = useSurfaceStyles();
  return (
    <div className={styles.root}>
      {breadcrumb && breadcrumb.length > 0 && (
        <Breadcrumb aria-label={t('breadcrumb.label')} size="small">
          {breadcrumb.map((entry, i) => (
            <Fragment key={entry.to}>
              {i > 0 && <BreadcrumbDivider />}
              <BreadcrumbItem>
                <BreadcrumbLink entry={entry} />
              </BreadcrumbItem>
            </Fragment>
          ))}
        </Breadcrumb>
      )}
      <div className={styles.row}>
        <Title2 as="h2" block className={styles.title}>
          {title}
        </Title2>
      </div>
      {actions && (
        <Toolbar aria-label={t('page.commands', { title })} className={mergeClasses(surface.commandBar, styles.actions)}>
          {actions}
        </Toolbar>
      )}
    </div>
  );
}

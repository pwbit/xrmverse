import { Dropdown, makeStyles, mergeClasses, NavDrawer, NavDrawerBody, NavDrawerFooter, NavItem, NavSectionHeader, Option } from '@fluentui/react-components';
import { useEffect, useMemo, useState, type MouseEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { mainAspxUrl, parseMainAspx, type MainAspxState } from '../../apps/mainAspx';
import { localized, type Area, type SiteMapModel, type SubArea } from '../../model/sitemap';
import { t } from '../../shell/i18n/t';
import { SITEMAP_TABLE_ICONS, SiteMapDashboardIcon, SiteMapPageIcon, SiteMapTableIcon, SiteMapUrlIcon } from '../../shell/icons';
import { webResourceUrl } from '../../client-api/scriptHost';

/**
 * Where a subarea leads: a `/main.aspx` state inside the app, an external address opened in a new tab, or an address that
 * is not opened at all (`blocked`).
 */
export type SubAreaTarget = { state: MainAspxState; href: string } | { external: string } | { blocked: string };

/**
 * Only `http:`, `https:` and site-relative addresses become links. The site map is written by solution imports and
 * customizers and rendered for every app user, so a `javascript:`, `data:` or any other scheme must never reach an `href`.
 * The address is parsed as the browser would (which also drops tabs and new lines inside the scheme).
 */
export function isSafeUrl(url: string): boolean {
  try {
    const { protocol } = new URL(url, window.location.origin);
    return protocol === 'http:' || protocol === 'https:';
  } catch {
    return false;
  }
}

const WEB_RESOURCE = /^(?:\$webresource:|\/webresources\/)(.+)$/i;

export function subAreaTarget(sub: SubArea, appId: string): SubAreaTarget {
  const url = (sub.url ?? '').trim();
  const mainAspx = /^\/main\.aspx(\?.*)?$/i.exec(url);
  const query = mainAspx ? (mainAspx[1] ?? '') : undefined;
  let state: MainAspxState | undefined;
  if (sub.kind === 'entity' && sub.entity) {
    const fromUrl = query !== undefined ? parseMainAspx(query) : undefined;
    state = { pageType: 'entitylist', etn: sub.entity.toLowerCase() };
    if (fromUrl?.pageType === 'entitylist' && fromUrl.viewId) {
      state.viewId = fromUrl.viewId;
      if (fromUrl.viewType !== undefined) state.viewType = fromUrl.viewType;
    }
  } else if (query !== undefined) {
    state = parseMainAspx(query);
  } else if (sub.kind === 'webresource') {
    const name = WEB_RESOURCE.exec(url)?.[1];
    state = { pageType: 'webresource', ...(name ? { name } : {}) };
  } else if (sub.kind === 'custom') {
    state = { pageType: 'custom' };
  } else {
    return isSafeUrl(url) ? { external: url } : { blocked: url };
  }
  state = { ...state, appId };
  return { state, href: mainAspxUrl(state) };
}

/** The subarea a page belongs to: the same page, or for a record the list of its table. */
export function isCurrent(target: SubAreaTarget, current: MainAspxState): boolean {
  if (!('state' in target)) return false;
  const s = target.state;
  if (current.pageType === 'entityrecord') return s.pageType === 'entitylist' && s.etn === current.etn;
  if (s.pageType !== current.pageType) return false;
  switch (s.pageType) {
    case 'entitylist':
      return s.etn === current.etn;
    case 'dashboard':
      return s.id === current.id;
    case 'webresource':
    case 'custom':
      return s.name === current.name;
    default:
      return true;
  }
}

const useStyles = makeStyles({
  nav: { minHeight: '100%', flexShrink: 0 },
  disabled: { opacity: 0.6, cursor: 'not-allowed' },
  // Entries line up with their group's header, as in Power Apps (no indent).
  item: { marginInlineStart: 0 },
  icon: { width: '20px', height: '20px', objectFit: 'contain' },
  areas: { width: '100%', minWidth: 0 },
});

/** The entry's icon: its own `$webresource:` icon, else a Fluent icon for its table (or kind). */
function SubAreaIcon({ sub, className }: { sub: SubArea; className: string }) {
  if (sub.icon?.toLowerCase().startsWith('$webresource:')) {
    return <img className={className} src={webResourceUrl(sub.icon.slice('$webresource:'.length))} alt="" aria-hidden="true" />;
  }
  const Icon =
    sub.kind === 'entity'
      ? (SITEMAP_TABLE_ICONS[sub.entity?.toLowerCase() ?? ''] ?? SiteMapTableIcon)
      : sub.kind === 'dashboard'
        ? SiteMapDashboardIcon
        : sub.kind === 'url'
          ? SiteMapUrlIcon
          : SiteMapPageIcon;
  return <Icon aria-hidden="true" />;
}

export interface SiteMapNavProps {
  /** The trimmed sitemap. */
  siteMap: SiteMapModel;
  /** `usersettings.uilanguageid`. */
  lcid: number;
  appId: string;
  current: MainAspxState;
  /** The `nav` landmark's name (the app's name). */
  label: string;
}

/** The sitemap navigation: the groups and subareas of one area, and an area switcher when there are several. */
export function SiteMapNav({ siteMap, lcid, appId, current, label }: SiteMapNavProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const areaOfCurrent = useMemo(
    () =>
      siteMap.areas.find((a) =>
        a.groups.some((g) => g.subAreas.some((s) => isCurrent(subAreaTarget(s, appId), current))),
      ),
    [siteMap, appId, current],
  );
  const [chosen, setChosen] = useState<Area | undefined>(areaOfCurrent);
  // Opening a page of another area (a link, the back button) shows that area.
  useEffect(() => {
    if (areaOfCurrent) setChosen(areaOfCurrent);
  }, [areaOfCurrent]);
  const area = (chosen && siteMap.areas.includes(chosen) ? chosen : undefined) ?? areaOfCurrent ?? siteMap.areas[0];
  const title = (node: { id: string; titles: Record<number, string>; firstLcid?: number }) =>
    localized(node.titles, lcid, node.id, node.firstLcid);
  const valueOf = (groupId: string, subId: string) => `${area?.id}/${groupId}/${subId}`;
  const selected = area?.groups
    .flatMap((g) => g.subAreas.map((sub) => ({ g, sub })))
    .find(({ sub }) => {
      const target = subAreaTarget(sub, appId);
      return 'href' in target && isCurrent(target, current);
    });
  // Plain clicks on internal links stay in the single-page app; modified clicks (new tab) keep the browser's default.
  const go = (href: string) => (e: MouseEvent) => {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    navigate(href);
  };

  return (
    <NavDrawer
      aria-label={label}
      type="inline"
      open
      className={styles.nav}
      // Always a string: Fluent treats an undefined first value as uncontrolled and would then ignore later ones.
      selectedValue={selected ? valueOf(selected.g.id, selected.sub.id) : ''}
    >
      <NavDrawerBody>
        {area?.groups.map((group) => (
          <div key={group.id}>
            <NavSectionHeader>{title(group)}</NavSectionHeader>
            {group.subAreas.map((sub) => {
              const target = subAreaTarget(sub, appId);
              const value = valueOf(group.id, sub.id);
              if ('blocked' in target) {
                return (
                  <NavItem
                    key={sub.id}
                    as="button"
                    value={value}
                    icon={<SubAreaIcon sub={sub} className={styles.icon} />}
                    className={mergeClasses(styles.item, styles.disabled)}
                    aria-disabled="true"
                    title={t('apps.nav.blockedUrl')}
                    onClick={(e) => e.preventDefault()}
                  >
                    {title(sub)}
                  </NavItem>
                );
              }
              if ('external' in target) {
                return (
                  <NavItem
                    key={sub.id}
                    as="a"
                    value={value}
                    icon={<SubAreaIcon sub={sub} className={styles.icon} />}
                    className={styles.item}
                    href={target.external}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    {title(sub)}
                  </NavItem>
                );
              }
              return (
                <NavItem
                  key={sub.id}
                  as="a"
                  value={value}
                  icon={<SubAreaIcon sub={sub} className={styles.icon} />}
                  className={styles.item}
                  href={target.href}
                  onClick={go(target.href)}
                >
                  {title(sub)}
                </NavItem>
              );
            })}
          </div>
        ))}
      </NavDrawerBody>
      {siteMap.areas.length > 1 && (
        <NavDrawerFooter>
          {/* The area switcher: a Fluent Dropdown (combo box) of the site map's areas. */}
          <Dropdown
            className={styles.areas}
            aria-label={t('apps.nav.areas')}
            value={area ? title(area) : ''}
            selectedOptions={area ? [area.id] : []}
            onOptionSelect={(_, d) => setChosen(siteMap.areas.find((a) => a.id === d.optionValue))}
          >
            {siteMap.areas.map((a) => (
              <Option key={a.id} value={a.id}>
                {title(a)}
              </Option>
            ))}
          </Dropdown>
        </NavDrawerFooter>
      )}
    </NavDrawer>
  );
}

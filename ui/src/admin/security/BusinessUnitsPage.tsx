import { Button, makeStyles, mergeClasses, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { ConfirmDialog } from '../common/ConfirmDialog';
import { RowCommand } from '../common/rowSelection';
import { usePagedList } from '../common/usePagedList';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import { useNotify } from '../../shell/notify/Notifications';
import { BuNameDialog } from './BuNameDialog';
import { PageHeader } from '../../shell/layout/PageHeader';

// No $select: Dataverse's `isdisabled` is read when the server has the column and ignored otherwise.
type Bu = { businessunitid: string; name?: string | null; isdisabled?: boolean | null };

/** What the page asks of a tree node after a write: show a new child under it, or re-read the level it sits in. */
type NodeHandle = { childCreated: () => void; changed: () => void };

/** The selection of the tree, owned by the page; each node registers itself so the page can refresh it after a command. */
interface BuTreeContext {
  selectedId: string | undefined;
  /** Selects `bu`, or clears the selection when it is already selected. */
  toggle: (bu: Bu) => void;
  /** Keeps the page's copy of the selected unit current after its level is read again (a rename, a disable). */
  sync: (bu: Bu) => void;
  register: (id: string, handle: NodeHandle) => () => void;
}

const BuTree = createContext<BuTreeContext | undefined>(undefined);

const useStyles = makeStyles({
  name: { cursor: 'pointer', padding: '2px 6px', borderRadius: tokens.borderRadiusMedium },
  selected: { backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2 },
});

type Dialog = { kind: 'child' | 'rename' | 'disable'; bu: Bu };

/**
 * The business unit tree. A click on a unit's name selects it, and the page's command bar acts on the selected unit
 * (add a child, rename, enable or disable), as in a model-driven view.
 */
export function BusinessUnitsPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const navigate = useNavigate();
  const [selected, setSelected] = useState<Bu>();
  const [dialog, setDialog] = useState<Dialog>();
  const nodes = useRef(new Map<string, NodeHandle>());

  const toggle = useCallback((bu: Bu) => setSelected((s) => (s?.businessunitid === bu.businessunitid ? undefined : bu)), []);
  const sync = useCallback((bu: Bu) => setSelected((s) => (s?.businessunitid === bu.businessunitid ? bu : s)), []);
  const register = useCallback((id: string, handle: NodeHandle) => {
    nodes.current.set(id, handle);
    return () => {
      if (nodes.current.get(id) === handle) nodes.current.delete(id);
      // A unit that leaves the tree (its parent collapsed or re-read) is no longer selected.
      setSelected((s) => (s?.businessunitid === id ? undefined : s));
    };
  }, []);
  const selectedId = selected?.businessunitid;
  const tree = useMemo(() => ({ selectedId, toggle, sync, register }), [selectedId, toggle, sync, register]);

  async function write(action: () => Promise<unknown>, success: string): Promise<boolean> {
    if (!api) return false;
    try {
      await action();
      notify.success(success);
      return true;
    } catch (e) {
      notify.error(e);
      return false;
    }
  }

  async function setDisabled(bu: Bu, isdisabled: boolean) {
    const ok = await write(
      () => api!.patch(`businessunits(${bu.businessunitid})`, { isdisabled }),
      t(isdisabled ? 'bu.disabled' : 'bu.enabled', { name: bu.name ?? '' }),
    );
    setDialog(undefined);
    if (ok) nodes.current.get(bu.businessunitid)?.changed();
  }

  const sel = selected;
  const selName = sel?.name ?? '';
  const target = dialog?.bu;
  const targetName = target?.name ?? '';

  return (
    <section>
      <PageHeader
        title={t('nav.businessUnits')}
        actions={
          sel && (
            <>
              {/* The unit's own record opens in the record form, where its columns can be edited. */}
              <RowCommand aria-label={t('bu.edit.open', { name: selName })} onClick={() => navigate(`/manage/tables/businessunit/records/${sel.businessunitid}`)}>
                {t('bu.edit.button')}
              </RowCommand>
              <RowCommand aria-label={t('bu.child.open', { name: selName })} onClick={() => setDialog({ kind: 'child', bu: sel })}>
                {t('bu.child.button')}
              </RowCommand>
              <RowCommand aria-label={t('bu.rename.open', { name: selName })} onClick={() => setDialog({ kind: 'rename', bu: sel })}>
                {t('bu.rename.button')}
              </RowCommand>
              {sel.isdisabled === true ? (
                <RowCommand aria-label={t('bu.enable.open', { name: selName })} onClick={() => void setDisabled(sel, false)}>
                  {t('bu.enable.button')}
                </RowCommand>
              ) : (
                <RowCommand aria-label={t('bu.disable.open', { name: selName })} onClick={() => setDialog({ kind: 'disable', bu: sel })}>
                  {t('bu.disable.button')}
                </RowCommand>
              )}
            </>
          )
        }
      />
      <BuTree.Provider value={tree}>
        <BuLevel parentId={null} />
      </BuTree.Provider>
      {dialog?.kind === 'child' && target && (
        <BuNameDialog
          title={t('bu.child.title', { name: targetName })}
          initial=""
          submitLabel={t('bu.child.submit')}
          onClose={() => setDialog(undefined)}
          onSubmit={async (childName) => {
            const ok = await write(
              () => api!.post('businessunits', { name: childName, 'parentbusinessunitid@odata.bind': `/businessunits(${target.businessunitid})` }),
              t('bu.created', { name: childName }),
            );
            if (ok) nodes.current.get(target.businessunitid)?.childCreated();
            return ok;
          }}
        />
      )}
      {dialog?.kind === 'rename' && target && (
        <BuNameDialog
          title={t('bu.rename.title', { name: targetName })}
          initial={targetName}
          submitLabel={t('bu.rename.submit')}
          onClose={() => setDialog(undefined)}
          onSubmit={async (newName) => {
            const ok = await write(() => api!.patch(`businessunits(${target.businessunitid})`, { name: newName }), t('bu.renamed', { name: newName }));
            if (ok) nodes.current.get(target.businessunitid)?.changed();
            return ok;
          }}
        />
      )}
      <ConfirmDialog
        open={dialog?.kind === 'disable'}
        title={t('bu.disable.title')}
        body={t('bu.disable.body', { name: targetName })}
        confirmLabel={t('bu.disable.confirm')}
        cancelLabel={t('common.cancel')}
        onConfirm={() => target && void setDisabled(target, true)}
        onCancel={() => setDialog(undefined)}
      />
    </section>
  );
}

/** The units under one parent (the roots for `null`), paged; each unit loads its own children when expanded. */
function BuLevel({ parentId }: { parentId: string | null }) {
  const parentFilter = encodeURIComponent(`_parentbusinessunitid_value eq ${parentId ?? 'null'}`);
  const list = usePagedList<Bu>(`businessunits?$filter=${parentFilter}&$orderby=name`);
  return (
    <>
      {list.error && (
        <MessageBar intent="error">
          <MessageBarBody>{t('bu.loadFailed', { detail: list.error })}</MessageBarBody>
        </MessageBar>
      )}
      {!list.loading && !list.error && list.items.length === 0 && parentId === null && <p>{t('bu.empty')}</p>}
      {list.items.length > 0 && (
        <ul role={parentId === null ? 'tree' : 'group'} aria-label={parentId === null ? t('nav.businessUnits') : undefined} style={{ listStyle: 'none', paddingInlineStart: parentId === null ? 0 : 24 }}>
          {list.items.map((bu) => (
            <BuNode key={bu.businessunitid} bu={bu} isRoot={parentId === null} onChanged={list.reload} />
          ))}
        </ul>
      )}
      {list.loading && <Spinner size="tiny" label={t('common.loading')} />}
      {list.nextLink && !list.loading && <Button onClick={list.loadMore}>{t('common.loadMore')}</Button>}
    </>
  );
}

function BuNode({ bu, isRoot, onChanged }: { bu: Bu; isRoot: boolean; onChanged: () => void }) {
  const styles = useStyles();
  const { selectedId, toggle, sync, register } = useContext(BuTree)!;
  const [expanded, setExpanded] = useState(isRoot);
  const [childVersion, setChildVersion] = useState(0);
  const name = bu.name ?? '';
  const id = bu.businessunitid;
  const isSelected = selectedId === id;

  useEffect(
    () =>
      register(id, {
        childCreated: () => {
          setExpanded(true);
          setChildVersion((v) => v + 1);
        },
        changed: onChanged,
      }),
    [id, register, onChanged],
  );
  useEffect(() => {
    if (isSelected) sync(bu);
  }, [isSelected, bu, sync]);

  return (
    <li role="treeitem" aria-expanded={expanded} aria-selected={isSelected}>
      <Button appearance="transparent" aria-label={t(expanded ? 'bu.collapse' : 'bu.expand', { name })} onClick={() => setExpanded((e) => !e)}>
        {t(expanded ? 'bu.glyph.expanded' : 'bu.glyph.collapsed')}
      </Button>
      <span
        tabIndex={0}
        className={mergeClasses(styles.name, isSelected && styles.selected)}
        onClick={() => toggle(bu)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            toggle(bu);
          }
        }}
      >
        {name}
      </span>
      {bu.isdisabled === true && <em> {t('bu.status.disabled')}</em>}
      {expanded && <BuLevel key={childVersion} parentId={id} />}
    </li>
  );
}

import { Field, makeStyles, Spinner } from '@fluentui/react-components';
import { useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { sameValue } from '../admin/tables/formValues';
import type { AttributeMeta } from '../admin/tables/metadata';
import { DisplayZoneContext } from '../apps/displayZone';
import { useLoadedUserSettings } from '../apps/userSettings';
import { fromClientValue } from '../client-api/values';
import { XrmHostContext } from '../client-api/xrm';
import type { Cell, CustomControlRef, FieldControl as FieldControlModel } from '../model/form';
import type { ControlEnv } from '../renderers/classic/form/controlEnv';
import { useActiveTheme } from '../renderers/ThemeScope';
import { useAuth } from '../shell/auth/AuthProvider';
import { t, uiLocale } from '../shell/i18n/t';
import { boundParameter, buildContext, literalParameter, updateContext, type PcfContext, type PcfParameter, type PcfView } from './context';
import { createFormatting } from './formatting';
import { hostInput, PcfBoundary, reasonOf, resetPcfHost, resizeTracker, startControl, userPrivileges, type Privileges, type PcfRunning } from './host';
import { loadControl, manifestFetcher, type PcfConstructor, type PcfControl } from './loader';
import type { PcfManifest } from './manifest';
import { loadStrings } from './resx';

/**
 * A field control that a PCF control renders (rulings R7–R10): it loads the control, builds its context, calls `init` once,
 * `updateView` whenever a bound value, the mode or the size changes, writes what the control reports through
 * `notifyOutputChanged` as a user edit, and calls `destroy` when the cell goes. When the control cannot run, the standard
 * control (`fallback`) takes its place and the form shows why.
 */

const useStyles = makeStyles({
  host: { minWidth: 0 },
});

/** What a manifest property shows: a column of the record, or a literal. */
type Binding = { kind: 'column'; meta: AttributeMeta } | { kind: 'literal'; parameter: PcfParameter };

interface Ready {
  manifest: PcfManifest;
  ctor: PcfConstructor;
  strings: Record<string, string>;
  bindings: Record<string, Binding>;
  privileges: Privileges;
}

type Phase = { status: 'loading' } | ({ status: 'ready' } & Ready) | { status: 'failed'; reason: string };

class FailureReason extends Error {}

/** The manifest's properties bound to the record's columns or given as literals (ruling R8). */
function bindingsOf(manifest: PcfManifest, custom: CustomControlRef, cellColumn: string, columns: AttributeMeta[]): Record<string, Binding> {
  if (manifest.dataSets.length > 0) throw new FailureReason(t('record.customControl.dataset'));
  const out: Record<string, Binding> = {};
  let first = true;
  for (const property of manifest.properties) {
    if (property.usage === 'output') continue;
    const given = custom.parameters[property.name];
    const type = property.ofType || property.types[0];
    if (given?.static) {
      out[property.name] = { kind: 'literal', parameter: literalParameter(given.value, given.type ?? type) };
      continue;
    }
    // The first bound property without a parameter is the cell's own column.
    const column = given?.value || (property.usage === 'bound' && first ? cellColumn : undefined);
    if (property.usage === 'bound') first = false;
    if (column) {
      const meta = columns.find((c) => c.logicalName === column.toLowerCase());
      if (!meta) throw new FailureReason(t('record.customControl.missingColumn', { property: property.name, column }));
      out[property.name] = { kind: 'column', meta };
    } else {
      out[property.name] = { kind: 'literal', parameter: property.defaultValue !== undefined ? literalParameter(property.defaultValue, type) : { raw: null, formatted: '' } };
    }
  }
  return out;
}

/** Forgets the session caches of the PCF host (tests). */
export const resetPcfFieldControls = resetPcfHost;

/** Encodes what `updateView` depends on, per parameter, for `updatedProperties`. */
const signature = (p: PcfParameter): string =>
  JSON.stringify([p.raw instanceof Date ? p.raw.getTime() : p.raw, p.formatted, p.error, p.errorMessage, p.security?.editable]);

export interface PcfFieldControlProps {
  cell: Cell;
  control: FieldControlModel & { customControl: CustomControlRef };
  env: ControlEnv;
  /** The cell's column. */
  column: AttributeMeta;
  /** The label as the form shows it. */
  label: string;
  required: boolean;
  /** The field is read-only here: the form, the record, the FormXml or a script disabled it. */
  disabled: boolean;
  /** The field's error, if any. */
  error?: string;
  /** The standard control, shown when the custom control cannot run. */
  fallback: ReactNode;
}

export function PcfFieldControl({ cell, control, env, column, label, required, disabled, error, fallback }: PcfFieldControlProps) {
  const styles = useStyles();
  const { api } = useAuth();
  const host = useContext(XrmHostContext);
  const zone = useContext(DisplayZoneContext);
  const dateFormats = useLoadedUserSettings()?.formats;
  // The theme the control renders in, read when it starts (`fluentDesignLanguage`); a later change of look reloads the page.
  const activeTheme = useActiveTheme();
  const themeRef = useRef(activeTheme);
  themeRef.current = activeTheme;
  const name = control.customControl.name;
  const wrapperRef = useRef<HTMLDivElement>(null);
  const hostRef = useRef<HTMLDivElement>(null);
  const [phase, setPhase] = useState<Phase>({ status: 'loading' });
  const [element, setElement] = useState<ReactNode>(null);
  const [size, setSize] = useState({ width: -1, height: -1 });
  const [renderTick, setRenderTick] = useState(0);
  const { ctx, state, overlay } = env;

  const fail = (reason: string) => {
    setPhase((p) => (p.status === 'failed' ? p : { status: 'failed', reason }));
    overlay?.setFormNotification({ uniqueId: `pcf:${control.id}`, level: 'WARNING', message: t('record.customControl.failed', { name, reason }) });
  };

  useEffect(() => {
    if (!api) return;
    let live = true;
    (async (): Promise<Ready> => {
      const { manifest, ctor } = await loadControl(name, { fetchManifest: manifestFetcher(api) });
      const bindings = bindingsOf(manifest, control.customControl, control.attribute, ctx.entity.attributes);
      const [strings, privileges] = await Promise.all([loadStrings(name, manifest, env.lcid), userPrivileges(api, host)]);
      return { manifest, ctor, strings, bindings, privileges };
    })().then(
      (ready) => live && setPhase({ status: 'ready', ...ready }),
      (e: unknown) => live && fail(reasonOf(e)),
    );
    return () => {
      live = false;
    };
    // One load per control on this cell; the rest is read live when the control runs.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, name]);

  // The view the control sees now (ruling R8): computed on every render, sent to the control only when it changed.
  const ready = phase.status === 'ready' ? phase : undefined;
  const locale = uiLocale();
  const formatting = createFormatting(locale, { currencySymbol: env.currencySymbol, dateFormats });
  const parameters: Record<string, PcfParameter> = {};
  if (ready) {
    for (const [property, binding] of Object.entries(ready.bindings)) {
      if (binding.kind === 'literal') {
        parameters[property] = binding.parameter;
        continue;
      }
      const meta = binding.meta;
      const own = meta.logicalName === column.logicalName;
      const scripted = overlay?.attribute(meta.logicalName).requiredLevel;
      const requiredLevel = scripted === 'required' ? 'ApplicationRequired' : scripted === 'recommended' ? 'Recommended' : scripted === 'none' ? 'None' : meta.requiredLevel;
      parameters[property] = boundParameter(meta, state.values[meta.logicalName], {
        formatting,
        locale,
        dateFormats,
        currencySymbol: env.currencySymbol,
        editable: own ? !disabled : !disabled && !state.isReadOnly(meta.logicalName),
        zone,
        requiredLevel: own && required && requiredLevel === 'None' ? 'ApplicationRequired' : requiredLevel,
        error: own ? error : state.errors[meta.logicalName],
      });
    }
  }
  const latest = state.latest();
  const recordId = latest.id ?? ctx.fieldContext.record?.id ?? null;
  const primary = ctx.entity.primaryNameAttribute;
  const recordName = primary ? latest.original[primary] : undefined;
  const contextInfo = {
    entityId: recordId ?? '',
    entityTypeName: ctx.entity.logicalName,
    entityRecordName: typeof recordName === 'string' ? recordName : '',
  };
  const view: Omit<PcfView, 'updatedProperties' | 'parameters'> & { parameters: Record<string, PcfParameter> } = {
    parameters,
    mode: { isControlDisabled: disabled, isVisible: true, label, allocatedWidth: size.width, allocatedHeight: size.height, contextInfo },
    page: { entityId: recordId, entityTypeName: ctx.entity.logicalName, isPageReadOnly: state.readOnly },
  };
  const live = useRef({ view, disabled, state, env, renderTick });
  live.current = { view, disabled, state, env, renderTick };

  /** The running instance and what it was last shown. */
  const running = useRef<{ instance: PcfControl; context: PcfContext; sent: Record<string, string>; mode: string; page: string; tick: number; failed: boolean } | undefined>();

  useEffect(() => {
    if (phase.status !== 'ready') return;
    const { manifest, ctor, strings, bindings, privileges } = phase;
    const virtual = manifest.controlType === 'virtual';
    const stateKey = `${ctx.entity.logicalName}:${control.id}`;
    const tracker = resizeTracker(() => wrapperRef.current, setSize);
    let handle: PcfRunning;

    const notifyOutputChanged = (instance: PcfControl) => {
      const now = live.current;
      if (now.disabled) return;
      let outputs: Record<string, unknown>;
      try {
        outputs = instance.getOutputs?.() ?? {};
      } catch (e) {
        console.warn(`Custom control ${name}: getOutputs failed`, e);
        return;
      }
      for (const [property, value] of Object.entries(outputs ?? {})) {
        const binding = bindings[property];
        if (binding?.kind !== 'column') continue;
        const meta = binding.meta;
        if (meta.logicalName !== control.attribute && now.state.isReadOnly(meta.logicalName)) continue;
        const nav = (target: string) => now.env.ctx.fieldContext.lookups.find((r) => r.referencingAttribute === meta.logicalName && r.referencedEntity === target)?.nav;
        const uniqueId = `pcf:${property}`;
        let next;
        try {
          next = fromClientValue(meta, value, nav, zone ?? 'Etc/UTC');
        } catch (e) {
          now.env.overlay?.addControlNotification(cell, { uniqueId, level: 'ERROR', message: t('record.customControl.badOutput', { name, reason: reasonOf(e) }) });
          continue;
        }
        now.env.overlay?.clearControlNotification(cell, uniqueId);
        if (sameValue(meta, now.state.latest().values[meta.logicalName], next)) continue;
        // A user edit (ruling R9): the form becomes dirty and the column's OnChange runs.
        now.state.setValue(meta.logicalName, next, 'user');
      }
    };

    const input = hostInput({
      manifest,
      controlName: name,
      strings,
      locale: uiLocale(),
      lcid: env.lcid,
      currencySymbol: env.currencySymbol,
      timeZone: zone,
      dateFormats,
      host,
      api,
      privileges,
      stateKey,
      requestRender: () => setRenderTick((n) => n + 1),
      trackContainerResize: (track) => tracker.track(track),
      theme: themeRef.current,
    });

    const first = live.current;
    // The first updateView names every parameter.
    const context = buildContext(input, { ...first.view, updatedProperties: Object.keys(first.view.parameters) });
    try {
      handle = startControl({
        name,
        ctor,
        virtual,
        context,
        stateKey,
        parent: hostRef.current,
        notifyOutputChanged,
        onStop: () => {
          tracker.dispose();
          running.current = undefined;
        },
      });
    } catch (e) {
      fail(reasonOf(e));
      return;
    }
    running.current = {
      instance: handle.instance,
      context,
      sent: Object.fromEntries(Object.entries(first.view.parameters).map(([k, p]) => [k, signature(p)])),
      mode: JSON.stringify(first.view.mode),
      page: JSON.stringify(first.view.page),
      tick: first.renderTick,
      failed: false,
    };
    if (virtual) setElement(handle.element as ReactNode);
    overlay?.setElement(cell, { customControl: true });
    const started = handle;
    return () => {
      started.stop();
      overlay?.setElement(cell, { customControl: false });
    };
    // Once per loaded control: the instance lives as long as the cell.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  // updateView after a change of a bound value, the mode, the size, or a requestRender (ruling R9).
  useEffect(() => {
    const run = running.current;
    if (!run || run.failed) return;
    const updated = Object.entries(view.parameters)
      .filter(([k, p]) => run.sent[k] !== signature(p))
      .map(([k]) => k);
    const mode = JSON.stringify(view.mode);
    const page = JSON.stringify(view.page);
    const layout = JSON.parse(run.mode).allocatedWidth !== size.width || JSON.parse(run.mode).allocatedHeight !== size.height;
    if (updated.length === 0 && mode === run.mode && page === run.page && run.tick === renderTick) return;
    for (const k of updated) run.sent[k] = signature(view.parameters[k]!);
    run.mode = mode;
    run.page = page;
    run.tick = renderTick;
    updateContext(run.context, { ...view, updatedProperties: layout ? [...updated, 'layout'] : updated });
    try {
      const out = run.instance.updateView(run.context);
      if (phase.status === 'ready' && phase.manifest.controlType === 'virtual') setElement(out as ReactNode);
    } catch (e) {
      run.failed = true;
      fail(t('record.customControl.updateFailed', { message: reasonOf(e) }));
    }
  });

  if (phase.status === 'failed') return <>{fallback}</>;
  return (
    <Field label={label} required={required} validationMessage={error} validationState={error ? 'error' : 'none'}>
      <div ref={wrapperRef} className={styles.host} data-pcf={name}>
        {phase.status === 'loading' && <Spinner size="tiny" label={t('record.customControl.loading')} />}
        <div ref={hostRef} />
        {ready?.manifest.controlType === 'virtual' && (
          <PcfBoundary onError={(e) => fail(t('record.customControl.renderFailed', { message: reasonOf(e) }))}>{element}</PcfBoundary>
        )}
      </div>
    </Field>
  );
}

import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { yaml } from '@codemirror/lang-yaml';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { lintGutter, setDiagnostics, type Diagnostic } from '@codemirror/lint';
import { Annotation, Compartment, EditorState, type Text } from '@codemirror/state';
import { EditorView, highlightActiveLine, highlightActiveLineGutter, keymap, lineNumbers } from '@codemirror/view';
import { makeStyles, tokens } from '@fluentui/react-components';
import { tags } from '@lezer/highlight';
import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react';
import { t } from '../../shell/i18n/t';
import type { EditorProblem } from './problems';

export interface CodeViewHandle {
  /** Moves the cursor to the 1-based `line` and `column` (clamped to the text), scrolls there and focuses the editor. */
  goTo(line: number, column?: number): void;
}

export interface CodeViewProps {
  text: string;
  onChange(text: string): void;
  /** Shown as lint diagnostics at their line (a problem without a line is not). */
  problems: EditorProblem[];
  /** While true (a save in flight) the text cannot be changed. */
  readOnly?: boolean;
}

const useStyles = makeStyles({
  root: {
    border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    overflow: 'hidden',
    minHeight: '320px',
    display: 'flex',
    flexDirection: 'column',
    '& .cm-editor': { flexGrow: 1 },
  },
});

/** The editor's look, from Fluent tokens only (the CSS variables of the current theme, light or dark). */
const theme = EditorView.theme({
  '&': {
    color: tokens.colorNeutralForeground1,
    backgroundColor: tokens.colorNeutralBackground1,
    fontSize: tokens.fontSizeBase300,
    height: '100%',
  },
  '.cm-scroller': { fontFamily: tokens.fontFamilyMonospace, lineHeight: tokens.lineHeightBase300 },
  '.cm-content': { caretColor: tokens.colorNeutralForeground1 },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: tokens.colorNeutralForeground1 },
  '&.cm-focused': { outline: `${tokens.strokeWidthThick} solid ${tokens.colorStrokeFocus2}` },
  '&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection': { backgroundColor: tokens.colorNeutralBackground1Selected },
  '.cm-activeLine': { backgroundColor: tokens.colorSubtleBackgroundHover },
  '.cm-gutters': { backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground3, borderRight: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}` },
  '.cm-activeLineGutter': { backgroundColor: tokens.colorSubtleBackgroundHover },
  '.cm-tooltip': { backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1, border: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke1}` },
  '.cm-diagnostic-error': { borderLeftColor: tokens.colorPaletteRedBorder2 },
  '.cm-diagnostic-warning': { borderLeftColor: tokens.colorPaletteYellowBorder2 },
  '.cm-lintRange-error': { backgroundImage: 'none', textDecorationLine: 'underline', textDecorationStyle: 'wavy', textDecorationColor: tokens.colorPaletteRedForeground1 },
  '.cm-lintRange-warning': { backgroundImage: 'none', textDecorationLine: 'underline', textDecorationStyle: 'wavy', textDecorationColor: tokens.colorPaletteYellowForeground2 },
});

const highlight = HighlightStyle.define([
  { tag: [tags.propertyName, tags.definition(tags.propertyName)], color: tokens.colorPaletteBlueForeground2 },
  { tag: [tags.string, tags.special(tags.string)], color: tokens.colorPaletteGreenForeground2 },
  { tag: [tags.number, tags.bool, tags.null, tags.keyword, tags.atom], color: tokens.colorPaletteMarigoldForeground2 },
  { tag: [tags.comment, tags.lineComment, tags.blockComment], color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  { tag: [tags.punctuation, tags.separator, tags.squareBracket, tags.brace], color: tokens.colorNeutralForeground2 },
  { tag: [tags.meta, tags.labelName, tags.typeName], color: tokens.colorPaletteBerryForeground2 },
]);

/** Marks a change that comes from the `text` prop. */
const external = Annotation.define<boolean>();

/** The offset of a 1-based line and column, clamped to the text. */
function offsetOf(doc: Text, line: number, column = 1): number {
  const at = doc.line(Math.min(Math.max(line, 1), doc.lines));
  return at.from + Math.min(Math.max(column - 1, 0), at.length);
}

function diagnosticsOf(doc: Text, problems: EditorProblem[]): Diagnostic[] {
  return problems
    .filter((p) => p.line !== undefined)
    .map((p) => {
      const from = offsetOf(doc, p.line!, p.column);
      const lineEnd = doc.lineAt(from).to;
      return { from, to: Math.max(from, lineEnd), severity: p.severity, message: p.message };
    });
}

/**
 * The YAML code view (CodeMirror 6): the text with line numbers, YAML highlighting, undo history and the problems as lint
 * diagnostics at their lines. The view owns its editing state; a text from outside (a canvas edit, a save that added the id)
 * replaces the document, and typing reports the new text through `onChange`.
 */
export const CodeView = forwardRef<CodeViewHandle, CodeViewProps>(function CodeView({ text, onChange, problems, readOnly = false }, ref) {
  const styles = useStyles();
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView>();
  const editable = useRef(new Compartment());
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;

  useEffect(() => {
    const created = new EditorView({
      parent: host.current!,
      state: EditorState.create({
        doc: text,
        extensions: [
          lineNumbers(),
          highlightActiveLineGutter(),
          highlightActiveLine(),
          history(),
          keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
          yaml(),
          syntaxHighlighting(highlight),
          lintGutter(),
          theme,
          editable.current.of(EditorState.readOnly.of(readOnly)),
          EditorView.contentAttributes.of({ 'aria-label': t('studio.editor.code.label') }),
          EditorView.updateListener.of((update) => {
            // A text set from outside is already the caller's: only the editor's own changes are reported.
            if (update.docChanged && update.transactions.some((tr) => tr.docChanged && !tr.annotation(external))) {
              onChangeRef.current(update.state.doc.toString());
            }
          }),
        ],
      }),
    });
    view.current = created;
    return () => {
      created.destroy();
      view.current = undefined;
    };
    // The view is made once; later texts, problems and the read-only flag are dispatched to it below.
  }, []);

  useEffect(() => {
    view.current?.dispatch({ effects: editable.current.reconfigure(EditorState.readOnly.of(readOnly)) });
  }, [readOnly]);

  useEffect(() => {
    const v = view.current;
    if (!v || v.state.doc.toString() === text) return;
    v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: text }, annotations: external.of(true) });
  }, [text]);

  useEffect(() => {
    const v = view.current;
    if (!v) return;
    v.dispatch(setDiagnostics(v.state, diagnosticsOf(v.state.doc, problems)));
  }, [problems, text]);

  useImperativeHandle(ref, () => ({
    goTo(line: number, column?: number) {
      const v = view.current;
      if (!v) return;
      v.dispatch({ selection: { anchor: offsetOf(v.state.doc, line, column) }, scrollIntoView: true });
      v.focus();
    },
  }));

  return <div ref={host} className={styles.root} />;
});

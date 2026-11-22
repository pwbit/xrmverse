import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Document } from 'yaml';
import type { XrmApi } from '../../shell/api/client';
import { errorText } from '../../shell/api/errors';
import { applyWorkflow, type ApplyResult } from '../api/studioApi';
import { parseText } from '../model/read';
import type { ModelProblem, WorkflowModel } from '../model/types';

/** How long the document must stay unchanged before the live dry run (spec §4.4). */
export const DRY_RUN_DELAY_MS = 800;

export interface EditorDocument {
  /** The document's text: what the code view shows and what a save sends. */
  text: string;
  /** The parsed document (comments kept) of `text`. Treat it as read-only: change the document through `mutate`. */
  doc: Document.Parsed;
  /** The typed model, when the reader found no problem. */
  model?: WorkflowModel;
  /** The reader's problems (syntax, shape). While there are some, there is no model, no dry run and no save. */
  localProblems: ModelProblem[];
  /** The last answer of the server (the live dry run, or a refused save) for the current text's lineage. */
  serverResult?: ApplyResult;
  /** The message of a dry run that failed (not a refusal: the server could not answer). */
  checkError?: string;
  /** Whether the text differs from the last saved (or loaded) text. */
  dirty: boolean;
  /**
   * A change of a save (the `id` of a first save) that the current text could not take yet (`markSaved`). While it waits the
   * text stays dirty, the editor must not save (a save without the id would create a second process), and the change is
   * applied as soon as the text can take it.
   */
  changePending: boolean;
  setText(text: string): void;
  /**
   * Applies an `edit.ts` operation to a fresh parse of the text and takes the result as the new text (`doc.toString()`), so
   * comments survive and a failed operation changes nothing. Returns what `fn` returned (a new step's path, ...).
   */
  mutate<T>(fn: (doc: Document.Parsed) => T): T;
  /**
   * `sent` is now the saved definition: it becomes the clean state. `change` (the `id` of a first save, ruling W4) is applied
   * to the saved text and to the current text. When the text changed while the save was in flight (a canvas or code edit),
   * the current text is kept (with `change`), stays dirty and is checked afresh (the save's answer, about `sent`, is
   * dropped); otherwise it becomes the saved text. A current text that cannot take `change` is kept as it is, and `change`
   * waits (`changePending`) until it can. Returns both texts and the change still waiting, if any.
   */
  markSaved(sent: string, change?: (doc: Document.Parsed) => void): { text: string; saved: string; pending?: DocumentChange };
  /** Shows a server answer (a save's) as the current one; a dry run still in flight is dropped. */
  showResult(result: ApplyResult): void;
}

/** A change to the document (the `id` of a first save). */
export type DocumentChange = (doc: Document.Parsed) => void;

/** `text` with `change` applied to a fresh parse of it (comments kept). */
function applyChange(text: string, change: (doc: Document.Parsed) => void): string {
  const { doc } = parseText(text);
  change(doc);
  return doc.toString();
}

/**
 * The editor's one document (spec §4.4): the text, its parsed `yaml` Document and derived model, and the live problems. 800 ms
 * after the last change a text without local problems is sent to `apply?dryRun=true`; every request has a sequence number and
 * an answer to a request older than the latest is dropped.
 */
export function useEditorDocument(
  api: XrmApi | undefined,
  initialText: string,
  initialSaved: string = initialText,
  /** A change a save could not yet put into `initialText` (handed over with it, see `markSaved`). */
  initialPending?: DocumentChange,
): EditorDocument {
  const [text, setTextState] = useState(initialText);
  const [pending, setPending] = useState<{ change: DocumentChange } | undefined>(initialPending && { change: initialPending });
  const [saved, setSaved] = useState(initialSaved);
  const [serverResult, setServerResult] = useState<ApplyResult>();
  /** Bumped to check the same text again (a save kept a text other than the one it sent). */
  const [revision, setRevision] = useState(0);
  const [checkError, setCheckError] = useState<string>();
  const parsed = useMemo(() => parseText(text), [text]);
  const seq = useRef(0);
  const textRef = useRef(text);
  textRef.current = text;

  const hasLocalProblems = parsed.problems.length > 0;
  useEffect(() => {
    // Any change makes the answers in flight stale.
    const request = ++seq.current;
    if (hasLocalProblems || !api) {
      setServerResult(undefined);
      setCheckError(undefined);
      return;
    }
    const timer = setTimeout(() => {
      applyWorkflow(api, text, { dryRun: true }).then(
        (result) => {
          if (request !== seq.current) return;
          setServerResult(result);
          setCheckError(undefined);
        },
        (e: unknown) => {
          if (request !== seq.current) return;
          setCheckError(errorText(e));
        },
      );
    }, DRY_RUN_DELAY_MS);
    return () => clearTimeout(timer);
  }, [api, text, hasLocalProblems, revision]);

  const setText = useCallback((next: string) => setTextState(next), []);

  const mutate = useCallback(<T,>(fn: (doc: Document.Parsed) => T): T => {
    const { doc } = parseText(textRef.current);
    const out = fn(doc);
    const next = doc.toString();
    textRef.current = next;
    setTextState(next);
    return out;
  }, []);

  const markSaved = useCallback((sent: string, change?: (doc: Document.Parsed) => void) => {
    const savedText = change ? applyChange(sent, change) : sent;
    // An edit made while the save was in flight is kept (ruling R7); the change goes into it when it can take it.
    const current = textRef.current;
    let next = savedText;
    let waiting: DocumentChange | undefined;
    if (current !== sent) {
      next = current;
      if (change) {
        try {
          next = applyChange(current, change);
        } catch {
          // A text that cannot take the change (broken while the save ran) is kept as it is, and the change waits for it
          // (`changePending`): the editor blocks Save and says so until the text can take it.
          waiting = change;
        }
      }
    }
    textRef.current = next;
    setTextState(next);
    setSaved(savedText);
    setPending(waiting && { change: waiting });
    if (current !== sent) {
      // The save's answer (and the edit's dry run it dropped) is about another text: the kept text is checked afresh.
      setServerResult(undefined);
      setCheckError(undefined);
      setRevision((r) => r + 1);
    }
    return { text: next, saved: savedText, pending: waiting };
  }, []);

  // A waiting change goes in as soon as the text can take it; until then it stays visible as `changePending`.
  useEffect(() => {
    if (!pending) return;
    let next: string;
    try {
      next = applyChange(text, pending.change);
    } catch {
      // Not yet: the change keeps waiting, and `changePending` keeps Save blocked and the message shown.
      return;
    }
    textRef.current = next;
    setTextState(next);
    setPending(undefined);
  }, [text, pending]);

  const showResult = useCallback((result: ApplyResult) => {
    seq.current++;
    setServerResult(result);
    setCheckError(undefined);
  }, []);

  return {
    text,
    doc: parsed.doc,
    model: parsed.model,
    localProblems: parsed.problems,
    serverResult,
    checkError,
    dirty: text !== saved || pending !== undefined,
    changePending: pending !== undefined,
    setText,
    mutate,
    markSaved,
    showResult,
  };
}

import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AttachmentPreview, previewKind } from './AttachmentPreview';
import type { Attachment } from './attachments';

const items: Attachment[] = [
  { id: 'a1', fileName: 'invoice-1042.pdf', size: 188416, mimeType: 'application/pdf' },
  { id: 'a2', fileName: 'tracking.png', size: 63488, mimeType: 'image/png' },
  { id: 'a3', fileName: 'notes.txt', size: 12, mimeType: 'text/plain' },
  { id: 'a4', fileName: 'page.html', size: 40, mimeType: 'text/html' },
  { id: 'a5', fileName: 'logo.svg', size: 40, mimeType: 'image/svg+xml' },
  { id: 'a6', fileName: 'data.xlsx', size: 40, mimeType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' },
  // Claims to be a PDF, but holds markup: never framed.
  { id: 'a7', fileName: 'fake.pdf', size: 40, mimeType: 'application/pdf' },
  // PDF bytes under an image's name and type: an image, never a frame.
  { id: 'a8', fileName: 'scan.png', size: 40, mimeType: 'image/png' },
];

let created: string[] = [];
let blobs: Blob[] = [];
const revoke = vi.fn();

beforeEach(() => {
  created = [];
  blobs = [];
  revoke.mockClear();
  let n = 0;
  Object.assign(URL, {
    createObjectURL: vi.fn((blob: Blob) => {
      blobs.push(blob);
      const url = `blob:preview-${++n}`;
      created.push(url);
      return url;
    }),
    revokeObjectURL: revoke,
  });
});

const CONTENT: Record<string, string> = { 'notes.txt': 'hello notes', 'invoice-1042.pdf': '%PDF-1.4 x', 'fake.pdf': '<html><script>alert(1)</script>', 'scan.png': '%PDF-1.4 x' };
const load = vi.fn(async (a: Attachment) => ({ name: a.fileName, blob: new Blob([CONTENT[a.fileName] ?? 'x'], { type: a.mimeType }) }));

function show(index = 0, onClose = vi.fn(), onDownload = vi.fn()) {
  return { onClose, onDownload, ...render(<AttachmentPreview items={items} index={index} load={load} onDownload={onDownload} onClose={onClose} />) };
}

describe('previewKind', () => {
  it('previews PDF, images and plain text; never HTML or SVG', () => {
    expect(items.map(previewKind)).toEqual(['pdf', 'image', 'text', 'none', 'none', 'none', 'pdf', 'image']);
    expect(previewKind({ id: 'x', fileName: 'a.PDF', size: 1 })).toBe('pdf');
    expect(previewKind({ id: 'x', fileName: 'a.htm', size: 1, mimeType: 'text/plain' })).toBe('none');
  });
});

describe('AttachmentPreview', () => {
  it('a real PDF (%PDF- bytes) opens in the browser viewer: a frame on an application/pdf blob, with the name, the size and n of m', async () => {
    show(0);
    const pane = await screen.findByRole('dialog', { name: 'invoice-1042.pdf' });
    expect(within(pane).getByText('184 KB · 1 of 8')).toBeInTheDocument();
    const frame = await within(pane).findByTitle('invoice-1042.pdf');
    expect(frame.tagName).toBe('IFRAME');
    // Chromium blocks its PDF viewer in any sandboxed frame: a verified PDF only, on a blob typed application/pdf.
    expect(frame).not.toHaveAttribute('sandbox');
    expect(frame.getAttribute('src')).toMatch(/^blob:preview-/);
    expect(blobs[0]?.type).toBe('application/pdf');
  });

  it('a .pdf without the %PDF- magic shows No preview, never a frame', async () => {
    show(6);
    expect(await screen.findByText('No preview')).toBeInTheDocument();
    expect(document.querySelector('iframe')).toBeNull();
    expect(created).toEqual([]);
  });

  it('PDF bytes under another type are never framed', async () => {
    show(7);
    expect(await screen.findByRole('img', { name: 'scan.png' })).toBeInTheDocument();
    expect(document.querySelector('iframe')).toBeNull();
  });

  it('an image as an image; next and previous move through the list and revoke the URL they leave', async () => {
    show(0);
    await screen.findByTitle('invoice-1042.pdf');
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    const img = await screen.findByRole('img', { name: 'tracking.png' });
    expect(img.getAttribute('src')).toMatch(/^blob:/);
    expect(revoke).toHaveBeenCalledWith(created[0]);
    await userEvent.click(screen.getByRole('button', { name: 'Previous' }));
    expect(await screen.findByTitle('invoice-1042.pdf')).toBeInTheDocument();
  });

  it('plain text as text', async () => {
    show(2);
    expect(await screen.findByText('hello notes')).toBeInTheDocument();
  });

  it('HTML, SVG and other types show No preview with Download, never rendered', async () => {
    const { onDownload } = show(3);
    expect(await screen.findByText('No preview')).toBeInTheDocument();
    expect(document.querySelector('iframe, img')).toBeNull();
    expect(load).not.toHaveBeenCalledWith(items[3]);
    await userEvent.click(screen.getByRole('button', { name: 'Download page.html' }));
    expect(onDownload).toHaveBeenCalledWith(items[3]);
  });

  it('Close revokes the blob URL', async () => {
    const { onClose, unmount } = show(0);
    await screen.findByTitle('invoice-1042.pdf');
    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(onClose).toHaveBeenCalled();
    unmount();
    await waitFor(() => expect(revoke).toHaveBeenCalledWith(created[0]));
  });
});

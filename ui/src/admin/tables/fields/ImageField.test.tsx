import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../../test/server';
import { App } from '../../../shell/App';
import { en } from '../../../shell/i18n/en';
import { onPath, signInAsAdministrator } from '../../security/testSupport';
import { installMetadata, rawAttr, rawEntity } from '../tableTestSupport';

const companions = (main: string) => [
  rawAttr(`${main}id`, 'Uniqueidentifier', { AttributeOf: main, IsValidForCreate: false, IsValidForUpdate: false }),
  rawAttr(`${main}_timestamp`, 'BigInt', { AttributeOf: main, IsValidForCreate: false, IsValidForUpdate: false }),
  rawAttr(`${main}_url`, 'String', { AttributeOf: main, IsValidForCreate: false, IsValidForUpdate: false }),
];

const card = rawEntity(
  'xv_card',
  'xv_cards',
  [
    rawAttr('xv_cardid', 'Uniqueidentifier', { IsPrimaryId: true, IsValidForUpdate: false }),
    rawAttr('xv_name', 'String', { IsPrimaryName: true, display: 'Name' }),
    rawAttr('xv_photo', 'Image', { display: 'Photo', CanStoreFullImage: true, MaxSizeInKB: 10240 }),
    ...companions('xv_photo'),
    rawAttr('xv_logo', 'Image', { display: 'Logo', CanStoreFullImage: false, MaxSizeInKB: 10240 }),
    ...companions('xv_logo'),
  ],
  { PrimaryIdAttribute: 'xv_cardid', PrimaryNameAttribute: 'xv_name', display: 'Card' },
);

// A 1×1 PNG and the start of a JPEG, as the server returns thumbnails (base64 in the record).
const PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==';
const JPEG = '/9j/4AAQSkZJRgABAQ==';
const RECORD = { xv_cardid: 'C1', xv_name: 'Ada', xv_photo: PNG, xv_logo: JPEG };

beforeEach(() => {
  signInAsAdministrator();
  installMetadata([card]);
});
afterEach(() => {
  sessionStorage.clear();
  vi.restoreAllMocks();
});

describe('ImageField', () => {
  it('Image_shows_thumbnail_and_full_download_only_when_allowed', async () => {
    let fullRequest: string | undefined;
    server.use(
      onPath('get', 'xv_cards(C1)', () => HttpResponse.json(RECORD)),
      onPath('get', 'xv_cards(C1)/xv_photo/$value', ({ request }) => {
        fullRequest = new URL(request.url).search;
        return new HttpResponse('full', { headers: { 'x-ms-file-name': 'ada.png' } });
      }),
    );
    Object.assign(URL, { createObjectURL: vi.fn(() => 'blob:fake'), revokeObjectURL: vi.fn() });
    let savedAs: string | undefined;
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      savedAs = this.download;
    });
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_card/records/C1']} />);

    const photo = await screen.findByRole('group', { name: 'Photo' });
    expect(within(photo).getByRole('img', { name: 'Photo thumbnail' })).toHaveAttribute('src', `data:image/png;base64,${PNG}`);
    const logo = screen.getByRole('group', { name: 'Logo' });
    expect(within(logo).getByRole('img', { name: 'Logo thumbnail' })).toHaveAttribute('src', `data:image/jpeg;base64,${JPEG}`);

    // The full image is offered only where the column keeps it (CanStoreFullImage).
    expect(within(logo).queryByRole('button', { name: 'Download full Logo' })).toBeNull();
    expect(within(logo).getByRole('button', { name: 'Upload Logo' })).toBeEnabled();
    expect(within(logo).getByRole('button', { name: 'Delete Logo' })).toBeEnabled();
    await user.click(within(photo).getByRole('button', { name: 'Download full Photo' }));
    await waitFor(() => expect(savedAs).toBe('ada.png'));
    expect(fullRequest).toBe('?size=full');
  });

  it('shows no thumbnail and no download for an empty image', async () => {
    server.use(onPath('get', 'xv_cards(C1)', () => HttpResponse.json({ ...RECORD, xv_photo: null })));
    render(<App initialEntries={['/manage/tables/xv_card/records/C1']} />);
    const photo = await screen.findByRole('group', { name: 'Photo' });
    expect(within(photo).getByText(en['field.image.none'])).toBeInTheDocument();
    expect(within(photo).queryByRole('img')).toBeNull();
    expect(within(photo).queryByRole('button', { name: 'Download full Photo' })).toBeNull();
    expect(within(photo).queryByRole('button', { name: 'Delete Photo' })).toBeNull();
  });

  it('uploads an image and shows the new thumbnail the server made', async () => {
    let reads = 0;
    let uploaded: string | null | undefined;
    server.use(
      onPath('get', 'xv_cards(C1)', () => {
        reads++;
        return HttpResponse.json(reads === 1 ? { ...RECORD, xv_photo: null } : { xv_photo: JPEG });
      }),
      onPath('patch', 'xv_cards(C1)/xv_photo', ({ request }) => {
        uploaded = request.headers.get('x-ms-file-name');
        return new HttpResponse(null, { status: 204 });
      }),
    );
    const user = userEvent.setup();
    render(<App initialEntries={['/manage/tables/xv_card/records/C1']} />);
    const photo = await screen.findByRole('group', { name: 'Photo' });
    await user.upload(within(photo).getByLabelText('File for Photo'), new File(['img'], 'me.jpg', { type: 'image/jpeg' }));
    expect(await within(photo).findByRole('img', { name: 'Photo thumbnail' })).toHaveAttribute('src', `data:image/jpeg;base64,${JPEG}`);
    expect(uploaded).toBe('me.jpg');
    expect(reads).toBe(2);
  });
});

'use client';

import * as React from 'react';
import { useTranslations } from 'next-intl';
import { Button } from '@mechware/ui/src/components/Button';
import { RETURN_PHOTO_ACCEPT, RETURN_PHOTO_MAX, canAddReturnPhoto, canRemoveReturnPhoto, problemCode, problemStatus, returnPhotoErrorKey, returnPhotoProblem, type ReturnPhoto } from '@mechware/api-client';
import { returnsApi } from '../../lib/returns-client';
import { ReturnAlert } from './ReturnParts';

/** Thumbnails come through the access-checked route (Bearer token), so each one is fetched as bytes and shown from a blob URL. */
function Thumb({ returnId, photo, alt }: { returnId: string; photo: ReturnPhoto; alt: string }) {
  const [src, setSrc] = React.useState<string | null>(null);
  React.useEffect(() => {
    let url: string | null = null;
    let dead = false;
    returnsApi.photoBytes(returnId, photo.id).then((b) => { if (!dead) { url = URL.createObjectURL(new Blob([b], { type: 'image/jpeg' })); setSrc(url); } }).catch(() => undefined);
    return () => { dead = true; if (url) URL.revokeObjectURL(url); };
  }, [returnId, photo.id]);
  // eslint-disable-next-line @next/next/no-img-element
  return src ? <img src={src} alt={alt} width={photo.width} height={photo.height} className="h-28 w-28 rounded-md border border-surface-border object-cover" /> : <div aria-hidden="true" className="h-28 w-28 animate-pulse rounded-md bg-surface" />;
}

/** Up to five photos per return: add while Requested or Approved, delete only while Requested. */
export function ReturnPhotos({ returnId, orderNumber, status }: { returnId: string; orderNumber: string; status: string }) {
  const t = useTranslations('Returns.photos');
  const [photos, setPhotos] = React.useState<ReturnPhoto[]>([]);
  const [loaded, setLoaded] = React.useState(false);
  const [progress, setProgress] = React.useState<number | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);
  const input = React.useRef<HTMLInputElement>(null);
  const load = React.useCallback(() => returnsApi.photos(returnId).then((p) => { setPhotos(p); setLoaded(true); }).catch(() => setLoaded(true)), [returnId]);
  React.useEffect(() => { void load(); }, [load]);

  const fail = (e: unknown) => { const key = returnPhotoErrorKey(problemStatus(e), problemCode(e)); setFailure(key); if (key === 'closed' || key === 'full') void load(); };
  const pick = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    const local = returnPhotoProblem(file, photos.length);
    if (local) { setFailure(local); return; }
    setFailure(null); setProgress(0);
    try {
      const added = await returnsApi.uploadPhoto(returnId, file, setProgress);
      setPhotos((p) => [...p, added].sort((a, b) => a.slot - b.slot));
    } catch (err) { fail(err); } finally { setProgress(null); }
  };
  const remove = async (id: string) => {
    setFailure(null);
    try { await returnsApi.removePhoto(returnId, id); setPhotos((p) => p.filter((x) => x.id !== id)); } catch (err) { fail(err); }
  };
  const canAdd = canAddReturnPhoto(status);
  if (!loaded || (!canAdd && photos.length === 0)) return null;
  return (
    <section aria-labelledby="return-photos" className="space-y-3 rounded-lg border border-surface-border p-4" data-testid="return-photos">
      <h2 id="return-photos" className="font-display text-lg font-bold text-text">{t('title', { count: photos.length, max: RETURN_PHOTO_MAX })}</h2>
      {photos.length > 0 && (
        <ul className="flex flex-wrap gap-3">
          {photos.map((p, i) => (
            <li key={p.id} className="space-y-1" data-testid="return-photo">
              <Thumb returnId={returnId} photo={p} alt={t('alt', { n: i + 1, order: orderNumber })} />
              {canRemoveReturnPhoto(status) && <Button type="button" variant="outline" size="sm" onClick={() => void remove(p.id)} aria-label={t('removeLabel', { n: i + 1 })}>{t('remove')}</Button>}
            </li>
          ))}
        </ul>
      )}
      {failure && <ReturnAlert>{t(`error.${failure}`)}</ReturnAlert>}
      {canAdd && (
        <div className="space-y-1">
          <input ref={input} type="file" accept={RETURN_PHOTO_ACCEPT} onChange={(e) => void pick(e)} className="sr-only" id="return-photo-input" data-testid="photo-input" disabled={progress !== null || photos.length >= RETURN_PHOTO_MAX} />
          <Button type="button" variant="outline" onClick={() => input.current?.click()} disabled={progress !== null || photos.length >= RETURN_PHOTO_MAX}>{t('add')}</Button>
          <p className="text-xs text-text-muted">{t('help', { max: RETURN_PHOTO_MAX })}</p>
          {progress !== null && <progress className="block h-2 w-full" max={1} value={progress} aria-label={t('uploading')} data-testid="photo-progress" />}
        </div>
      )}
    </section>
  );
}

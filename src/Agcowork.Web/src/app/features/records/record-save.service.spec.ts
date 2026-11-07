import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API, RECORD_ID, recordDto } from '../../core/testing/test-utils';
import { AUTOSAVE_DELAY_MS, RecordSaveService } from './record-save.service';

const RECORD_URL = `${API}/records/${RECORD_ID}`;
const LOAD_URL = `${RECORD_URL}?includeBody=true`;

describe('RecordSaveService', () => {
  let http: HttpTestingController;
  let service: RecordSaveService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), RecordSaveService],
    });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(RecordSaveService);
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  /** Loads the record (revision 3, body "Hallo"). */
  async function loaded(): Promise<void> {
    const loading = service.load('demo', RECORD_ID);
    http.expectOne(LOAD_URL).flush(recordDto({ body: 'Hallo' }));
    await loading;
  }

  const conflictBody = {
    code: 'revision_mismatch',
    status: 409,
    currentRevision: 5,
    changedKeys: ['status', 'body'],
  };

  it('load_Record_FillsEditsAndEditorValue', async () => {
    await loaded();

    expect(service.title()).toBe('Login bauen');
    expect(service.properties()).toEqual({ status: 'todo' });
    expect(service.editorValue()).toBe('Hallo');
    expect(service.isDirty()).toBe(false);
  });

  it('load_NotFound_SetsLoadError', async () => {
    const loading = service.load('demo', RECORD_ID);
    http
      .expectOne(LOAD_URL)
      .flush({ code: 'not_found', status: 404 }, { status: 404, statusText: 'Not Found' });
    await loading;

    expect(service.record()).toBeNull();
    expect(service.loadError()).toContain('Nicht gefunden');
  });

  it('save_NothingChanged_SendsNoRequest', async () => {
    await loaded();

    expect(await service.save()).toBe(true);
  });

  it('save_ChangedTitleAndProperty_PatchesOnlyChangesWithExpectedRevision', async () => {
    await loaded();
    service.setTitle('Login fertig');
    service.setProperty('status', 'done');

    const saving = service.save();
    const request = http.expectOne(RECORD_URL);
    expect(request.request.method).toBe('PATCH');
    expect(request.request.body).toEqual({
      title: 'Login fertig',
      properties: { status: 'done' },
      expectedRevision: 3,
    });
    request.flush(
      recordDto({ title: 'Login fertig', properties: { status: 'done' } as never, revision: 4 }),
    );

    expect(await saving).toBe(true);
    expect(service.isDirty()).toBe(false);
    expect(Number(service.record()?.revision)).toBe(4);
    expect(service.record()?.body).toBe('Hallo');
    expect(service.editorValue()).toBe('Hallo');
  });

  it('save_ClearedProperty_SendsNull', async () => {
    await loaded();
    service.setProperty('status', null);

    const saving = service.save();
    const request = http.expectOne(RECORD_URL);
    expect(request.request.body).toEqual({ properties: { status: null }, expectedRevision: 3 });
    request.flush(recordDto({ properties: {} as never, revision: 4 }));
    await saving;

    expect(service.properties()).toEqual({});
  });

  it('save_BodyChanged_SendsBody', async () => {
    await loaded();
    service.setBody('Neuer Text');

    const saving = service.save();
    const request = http.expectOne(RECORD_URL);
    expect(request.request.body).toEqual({ body: 'Neuer Text', expectedRevision: 3 });
    request.flush(recordDto({ revision: 4 }));
    await saving;

    expect(service.isDirty()).toBe(false);
    expect(service.editorValue()).toBe('Hallo');
  });

  it('save_TextTypedWhileSaving_StaysDirtyAndKeepsText', async () => {
    await loaded();
    service.setBody('Eins');

    const saving = service.save();
    const request = http.expectOne(RECORD_URL);
    service.setBody('Eins Zwei');
    request.flush(recordDto({ revision: 4 }));
    await saving;

    expect(service.body()).toBe('Eins Zwei');
    expect(service.isDirty()).toBe(true);
  });

  it('save_BeforeSaveHook_CanContributePendingBody', async () => {
    await loaded();
    service.beforeSave = () => service.setBody('Aus dem Editor');

    const saving = service.save();
    const request = http.expectOne(RECORD_URL);
    expect(request.request.body.body).toBe('Aus dem Editor');
    request.flush(recordDto({ revision: 4 }));
    await saving;
  });

  it('save_SecondCallWhileSaving_WaitsAndUsesNewRevision', async () => {
    await loaded();
    service.setTitle('A');
    const first = service.save();
    const firstRequest = http.expectOne(RECORD_URL);
    service.setTitle('B');
    const second = service.save();

    firstRequest.flush(recordDto({ title: 'A', revision: 4 }));
    await first;
    const secondRequest = await vi.waitFor(() => http.expectOne(RECORD_URL));
    expect(secondRequest.request.body).toEqual({ title: 'B', expectedRevision: 4 });
    secondRequest.flush(recordDto({ title: 'B', revision: 5 }));

    expect(await second).toBe(true);
  });

  describe('auto-save', () => {
    it('setTitle_AfterDelay_SavesOnce', async () => {
      await loaded();
      vi.useFakeTimers();

      service.setTitle('Eins');
      vi.advanceTimersByTime(AUTOSAVE_DELAY_MS - 1);
      http.expectNone(RECORD_URL);
      service.setTitle('Eins Zwei');
      vi.advanceTimersByTime(AUTOSAVE_DELAY_MS - 1);
      http.expectNone(RECORD_URL);
      vi.advanceTimersByTime(1);

      const request = http.expectOne(RECORD_URL);
      expect(request.request.body).toEqual({ title: 'Eins Zwei', expectedRevision: 3 });
      request.flush(recordDto({ title: 'Eins Zwei', revision: 4 }));
    });

    it('save_Explicit_CancelsPendingAutoSave', async () => {
      await loaded();
      vi.useFakeTimers();
      service.setTitle('Eins');

      const saving = service.save();
      http.expectOne(RECORD_URL).flush(recordDto({ title: 'Eins', revision: 4 }));
      await saving;
      vi.advanceTimersByTime(AUTOSAVE_DELAY_MS * 2);

      http.expectNone(RECORD_URL);
    });
  });

  describe('conflict', () => {
    async function conflicted(): Promise<void> {
      await loaded();
      service.setTitle('Meins');
      const saving = service.save();
      http.expectOne(RECORD_URL).flush(conflictBody, { status: 409, statusText: 'Conflict' });
      expect(await saving).toBe(false);
    }

    it('save_RevisionMismatch_ExposesConflictWithChangedKeys', async () => {
      await conflicted();

      expect(service.conflict()).toEqual({
        currentRevision: 5,
        changedKeys: ['status', 'body'],
      });
      expect(service.isDirty()).toBe(true);
    });

    it('save_WhileConflictOpen_DoesNotRequest', async () => {
      await conflicted();

      expect(await service.save()).toBe(false);
    });

    it('overwrite_AfterConflict_SendsWithoutExpectedRevision', async () => {
      await conflicted();

      const saving = service.overwrite();
      const request = http.expectOne(RECORD_URL);
      expect(request.request.body).toEqual({ title: 'Meins' });
      request.flush(recordDto({ title: 'Meins', revision: 6 }));
      // the other writer changed the body: it is fetched so the next save cannot clobber it
      await vi.waitFor(() =>
        http.expectOne(LOAD_URL).flush(recordDto({ body: 'Fremd', revision: 6 })),
      );

      expect(await saving).toBe(true);
      expect(service.conflict()).toBeNull();
      expect(Number(service.record()?.revision)).toBe(6);
      expect(service.editorValue()).toBe('Fremd');
      expect(service.body()).toBe('Fremd');
      expect(service.isDirty()).toBe(false);
    });

    it('overwrite_TitleEditedWhileFetchingBody_StillReplacesBody', async () => {
      await conflicted();

      const overwriting = service.overwrite();
      http.expectOne(RECORD_URL).flush(recordDto({ title: 'Meins', revision: 6 }));
      const get = await vi.waitFor(() => http.expectOne(LOAD_URL));
      service.setTitle('Noch eins');
      get.flush(recordDto({ body: 'Fremd', revision: 6 }));
      await overwriting;

      expect(service.editorValue()).toBe('Fremd');
      expect(service.title()).toBe('Noch eins');
    });

    it('overwrite_FetchFails_MarksBodyStaleAndNextBodySaveConflicts', async () => {
      await conflicted();

      const overwriting = service.overwrite();
      http.expectOne(RECORD_URL).flush(recordDto({ title: 'Meins', revision: 6 }));
      await vi.waitFor(() =>
        http
          .expectOne(LOAD_URL)
          .flush({ code: 'internal_error', status: 500 }, { status: 500, statusText: 'Error' }),
      );
      await overwriting;
      expect(service.bodyStale()).toBe(3);
      expect(service.saveError()).toContain('neu laden');

      service.setBody('Auf altem Stand');
      const saving = service.save();
      const request = http.expectOne(RECORD_URL);
      expect(request.request.body).toEqual({ body: 'Auf altem Stand', expectedRevision: 3 });
      request.flush(
        { code: 'revision_mismatch', status: 409, currentRevision: 6, changedKeys: ['body'] },
        { status: 409, statusText: 'Conflict' },
      );

      expect(await saving).toBe(false);
      expect(service.conflict()?.changedKeys).toEqual(['body']);
    });

    it('overwrite_TextTypedWhileFetchingBody_IsKeptAndNextSaveConflicts', async () => {
      await conflicted();

      const overwriting = service.overwrite();
      http.expectOne(RECORD_URL).flush(recordDto({ title: 'Meins', revision: 6 }));
      const get = await vi.waitFor(() => http.expectOne(LOAD_URL));
      service.beforeSave = () => service.setBody('Getippt');
      get.flush(recordDto({ body: 'Fremd', revision: 6 }));
      await overwriting;

      expect(service.body()).toBe('Getippt');
      expect(service.editorValue()).toBe('Hallo');
      expect(service.bodyStale()).toBe(3);
    });

    it('overwrite_OwnBodyEdit_DoesNotRefetchBody', async () => {
      await loaded();
      service.setBody('Mein Text');
      const saving = service.save();
      http.expectOne(RECORD_URL).flush(conflictBody, { status: 409, statusText: 'Conflict' });
      await saving;

      const overwriting = service.overwrite();
      http.expectOne(RECORD_URL).flush(recordDto({ revision: 6 }));
      await overwriting;

      expect(service.editorValue()).toBe('Hallo');
      expect(service.body()).toBe('Mein Text');
    });

    it('reload_AfterConflict_DiscardsEditsAndLoadsServerState', async () => {
      await conflicted();

      const reloading = service.reload();
      http.expectOne(LOAD_URL).flush(recordDto({ title: 'Fremd', body: 'Neu', revision: 5 }));
      await reloading;

      expect(service.conflict()).toBeNull();
      expect(service.isDirty()).toBe(false);
      expect(service.title()).toBe('Fremd');
      expect(service.editorValue()).toBe('Neu');
    });
  });

  it('save_ServerTrimmedText_KeepsLocalTextWithTrailingSpace', async () => {
    await loaded();
    service.setProperty('note', 'abc ');
    const saving = service.save();
    service.setTitle('Neu'); // second edit while in flight keeps the first key non-dirty after save
    http
      .expectOne(RECORD_URL)
      .flush(recordDto({ properties: { status: 'todo', note: 'abc' } as never, revision: 4 }));
    await saving;

    expect(service.properties()['note']).toBe('abc ');
  });

  it('save_RecordArchivedMeanwhile_ReloadsMetadataSoTheViewTurnsReadonly', async () => {
    await loaded();
    service.setTitle('x');

    const saving = service.save();
    http
      .expectOne(RECORD_URL)
      .flush({ code: 'record_archived', status: 409 }, { status: 409, statusText: 'Conflict' });
    await vi.waitFor(() =>
      http
        .expectOne(RECORD_URL)
        .flush(recordDto({ revision: 4, archivedAt: '2026-11-07T12:00:00Z' })),
    );

    expect(await saving).toBe(false);
    expect(service.record()?.archivedAt).toBe('2026-11-07T12:00:00Z');
    expect(service.record()?.body).toBe('Hallo');
  });

  describe('errors', () => {
    it('save_ValidationErrors_MapsToPropertyKeysAndTitle', async () => {
      await loaded();
      service.setTitle('');
      service.setProperty('estimate', -1);

      const saving = service.save();
      http.expectOne(RECORD_URL).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [
            { field: 'title', code: 'required', message: 'Title is required.' },
            { field: 'properties.estimate', code: 'out_of_range', message: 'Too small.' },
          ],
        },
        { status: 400, statusText: 'Bad Request' },
      );

      expect(await saving).toBe(false);
      expect(service.fieldErrors()).toEqual({
        title: ['Pflichtfeld.'],
        estimate: ['Wert liegt außerhalb des erlaubten Bereichs.'],
      });
      expect(service.saveError()).toContain('markierten Felder');
      expect(service.conflict()).toBeNull();
    });

    it('setProperty_AfterValidationError_ClearsThatFieldError', async () => {
      await loaded();
      service.setProperty('estimate', -1);
      const saving = service.save();
      http.expectOne(RECORD_URL).flush(
        {
          code: 'validation_failed',
          status: 400,
          errors: [{ field: 'properties.estimate', code: 'out_of_range', message: 'x' }],
        },
        { status: 400, statusText: 'Bad Request' },
      );
      await saving;

      service.setProperty('estimate', 2);

      expect(service.fieldErrors()['estimate']).toBeUndefined();
    });

    it('save_ArchivedRecord_ShowsDomainMessage', async () => {
      await loaded();
      service.setTitle('x');

      const saving = service.save();
      http
        .expectOne(RECORD_URL)
        .flush({ code: 'record_archived', status: 409 }, { status: 409, statusText: 'Conflict' });
      await vi.waitFor(() =>
        http.expectOne(RECORD_URL).flush(recordDto({ archivedAt: '2026-11-07T12:00:00Z' })),
      );

      expect(await saving).toBe(false);
      expect(service.saveError()).toContain('archiviert');
      expect(service.conflict()).toBeNull();
    });
  });
});

import { ApiError } from '../../core/api/api-error';
import { commentErrorMessages } from '../../features/comments/comment-errors';
import { describeError } from './api-error-text';

describe('describeError', () => {
  it('describeError_ArchiveCodes_HaveGermanTexts', () => {
    expect(describeError(new ApiError(409, 'collection_archived', [], 'server detail'))).toBe(
      'Die Collection ist archiviert, ihre Records sind schreibgeschützt.',
    );
    expect(describeError(new ApiError(409, 'space_archived', [], 'server detail'))).toBe(
      'Der Space ist archiviert, sein Inhalt ist schreibgeschützt.',
    );
    expect(describeError(new ApiError(409, 'workspace_archived', [], 'server detail'))).toBe(
      'Der Workspace ist archiviert und damit schreibgeschützt.',
    );
  });

  it('commentErrorMessages_ArchiveCodes_UseTheSharedTexts', () => {
    for (const code of ['collection_archived', 'space_archived', 'workspace_archived']) {
      const error = new ApiError(409, code, [], 'server detail');
      expect(commentErrorMessages(error, 'add')).toEqual([describeError(error)]);
    }
  });

  it('describeError_UnknownCode_FallsBackToServerDetail', () => {
    expect(describeError(new ApiError(409, 'something_new', [], 'Vom Server.'))).toBe(
      'Vom Server.',
    );
  });
});

#ifndef DA_DISC_DEVICES_H
#define DA_DISC_DEVICES_H
#ifdef __cplusplus
extern "C" {
#endif
/* Snapshot only. Caller owns the UTF-8 JSON and must da_free it. NULL = failure.
 * Call off the UI thread; OS discovery may block. No mount/unmount/write calls.
 * Tokens identify a published IOMedia instance in this process, not content.
 * Verify immediately before every source read and again after each bounded read;
 * a successful check cannot make a later pathname open/read atomic with eject.
 */
char *da_devices(void);
int da_verify(const char *drive, const char *token);
/* Evidence-based coverage warning: uncovered=true only for proven audio/multiple
 * sessions. Unknown coverage is described in note, not turned into failure.
 * Same allocation convention as da_devices. */
char *da_tracks(const char *drive, const char *token);
/* Enumerate an already safely opened directory FD via SDK-native dirent ABI.
 * Owns a dup only; caller retains fd. JSON {names:[...],error:string,note:string};
 * partial names survive enumeration errors. Cleanup diagnostics are note only
 * and never invalidate a completed listing. Same allocation/free convention. */
char *da_list_directory(int fd);
void da_free(void *pointer);
#ifdef __cplusplus
}
#endif
#endif

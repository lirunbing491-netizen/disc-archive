// C ABI around the exact bundled upstream 7-Zip Unix ABI; no COM marshaler.
#include "CPP/Common/MyInitGuid.h"
#include "CPP/7zip/Archive/IArchive.h"
#include <dlfcn.h>
#include <cstring>
#include <string>
#include <new>
#include <cstdio>
#include <cstdint>
#include <map>
#include <mutex>

using ReadFn = int (*)(void *, void *, uint32_t, uint32_t *);
using SeekFn = int (*)(void *, Int64, UInt32, UInt64 *);
using WriteFn = int (*)(void *, const void *, uint32_t, uint32_t *);
#define UNKNOWN_IMPL(iface) \
  ULONG refs = 1; \
  ULONG AddRef() throw() override { return ++refs; } \
  ULONG Release() throw() override { ULONG n = --refs; if (!n) delete this; return n; } \
  HRESULT QueryInterface(REFIID id, void **p) throw() override { \
    *p = nullptr; if (id == IID_IUnknown || id == IID_##iface) *p = static_cast<iface *>(this); \
    if (!*p) return E_NOINTERFACE; AddRef(); return S_OK; }
#define UNKNOWN_IMPL_WITH_BASE(iface, base) \
  ULONG refs = 1; \
  ULONG AddRef() throw() override { return ++refs; } \
  ULONG Release() throw() override { ULONG n = --refs; if (!n) delete this; return n; } \
  HRESULT QueryInterface(REFIID id, void **p) throw() override { \
    *p = nullptr; if (id == IID_IUnknown || id == IID_##iface || id == IID_##base) *p = static_cast<iface *>(this); \
    if (!*p) return E_NOINTERFACE; AddRef(); return S_OK; }
struct Input final : IInStream {
  UNKNOWN_IMPL_WITH_BASE(IInStream, ISequentialInStream)
  void *user; ReadFn read; SeekFn seek; UInt64 position = 0;
  Input(void *u, ReadFn r, SeekFn s) : user(u), read(r), seek(s) {}
  HRESULT Read(void *p, UInt32 n, UInt32 *done) throw() override {
    UInt64 actual; HRESULT h = seek(user, position, 0, &actual);
    if (h) return h;
    UInt32 count = 0; h = read(user, p, n, &count);
    if (count > n) return E_FAIL;
    position += count; if (done) *done = count; return h;
  }
  HRESULT Seek(Int64 off, UInt32 origin, UInt64 *result) throw() override {
    UInt64 actual; HRESULT h;
    if (origin == 1) { if (off < 0 && (UInt64)(-(off + 1)) + 1 > position) return E_FAIL;
      h = seek(user, position, 0, &actual); if (h) return h; }
    h = seek(user, off, origin, &actual); if (!h) { position = actual; if (result) *result = actual; } return h;
  }
};
struct Output final : ISequentialOutStream {
  UNKNOWN_IMPL(ISequentialOutStream)
  void *user; WriteFn write;
  Output(void *u, WriteFn w) : user(u), write(w) {}
  HRESULT Write(const void *p, UInt32 n, UInt32 *done) throw() override { return write(user, p, n, done); }
};
struct Extract final : IArchiveExtractCallback {
  UNKNOWN_IMPL_WITH_BASE(IArchiveExtractCallback, IProgress)
  UInt32 index; Output *output; bool requested = false; Int32 result = -1;
  Extract(UInt32 i, Output *o) : index(i), output(o) {}
  HRESULT SetTotal(UInt64) throw() override { return S_OK; }
  HRESULT SetCompleted(const UInt64 *) throw() override { return S_OK; }
  HRESULT GetStream(UInt32 i, ISequentialOutStream **p, Int32 mode) throw() override {
    *p = nullptr; if (i != index || mode || requested) return E_FAIL;
    requested = true; output->AddRef(); *p = output; return S_OK;
  }
  HRESULT PrepareOperation(Int32 mode) throw() override { return mode == 0 ? S_OK : E_FAIL; }
  HRESULT SetOperationResult(Int32 r) throw() override { result = r; return S_OK; }
};
struct Context { void *module = nullptr; IInArchive *archive = nullptr; Input *input = nullptr; std::string text; };
// Native codec modules register C++ runtime callbacks. Pin each absolute module
// once for the process; unloading between readers can leave exit callbacks in
// unmapped code (observed with a portable statically linked libc++ toolchain).
// Archive instances, source callbacks and item streams are still released fully.
static void *loadModule(const char *path) {
  static auto *gate = new std::mutex;
  static auto *modules = new std::map<std::string, void *>;
  std::lock_guard<std::mutex> lock(*gate);
  auto found = modules->find(path); if (found != modules->end()) return found->second;
  void *module = dlopen(path, RTLD_NOW | RTLD_LOCAL);
  if (module) modules->emplace(path, module);
  return module;
}
struct Item { Context *context; IInStream *stream; UInt64 sourcePosition; };
static void utf8(std::string &s, BSTR b) {
  s.clear(); if (!b) return;
  for (UINT i = 0, n = SysStringLen(b); i < n; ++i) {
    uint32_t c = b[i];
    if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n && b[i+1] >= 0xDC00 && b[i+1] <= 0xDFFF)
      c = 0x10000 + ((c - 0xD800) << 10) + (b[++i] - 0xDC00);
    if (c < 0x80) s += char(c);
    else if (c < 0x800) { s += char(0xC0 | (c >> 6)); s += char(0x80 | (c & 63)); }
    else if (c < 0x10000) { s += char(0xE0 | (c >> 12)); s += char(0x80 | ((c >> 6) & 63)); s += char(0x80 | (c & 63)); }
    else { s += char(0xF0 | (c >> 18)); s += char(0x80 | ((c >> 12) & 63)); s += char(0x80 | ((c >> 6) & 63)); s += char(0x80 | (c & 63)); }
  }
}
extern "C" {
int da_close(Context *c) {
  if (!c) return 0; int hr = 0;
  if (c->archive) { hr = c->archive->Close(); c->archive->Release(); }
  if (c->input) c->input->Release();
  delete c; return hr;
}
int da_open(const char *library, const char *handler, void *user, ReadFn read, SeekFn seek, Context **out) {
  *out = nullptr; Context *c = new(std::nothrow) Context; if (!c) return E_OUTOFMEMORY;
  c->module = loadModule(library);
  if (!c->module) { da_close(c); return E_FAIL; }
  auto count = (HRESULT (*)(UInt32 *))dlsym(c->module, "GetNumberOfFormats");
  auto prop = (HRESULT (*)(UInt32, PROPID, PROPVARIANT *))dlsym(c->module, "GetHandlerProperty2");
  auto create = (HRESULT (*)(const GUID *, const GUID *, void **))dlsym(c->module, "CreateObject");
  UInt32 n = 0; int hr = E_NOINTERFACE;
  if (!count || !prop || !create || count(&n)) { da_close(c); return E_FAIL; }
  for (UInt32 i = 0; i < n; ++i) {
    PROPVARIANT v = {}; hr = prop(i, 0, &v);
    if (hr) { VariantClear(&v); break; }
    if (v.vt == VT_BSTR) utf8(c->text, v.bstrVal); else c->text.clear();
    VariantClear(&v); if (c->text != handler) continue;
    hr = prop(i, 1, &v); GUID id;
    if (!hr && v.vt == VT_BSTR && SysStringByteLen(v.bstrVal) == sizeof(id)) {
      memcpy(&id, v.bstrVal, sizeof(id)); hr = create(&id, &IID_IInArchive, (void **)&c->archive);
    } else hr = E_FAIL;
    VariantClear(&v); break;
  }
  if (!c->archive) { da_close(c); return hr ? hr : E_NOINTERFACE; }
  c->input = new Input(user, read, seek); UInt64 start = 0;
  hr = c->archive->Open(c->input, &start, nullptr);
  if (hr) { da_close(c); return hr; } *out = c; return 0;
}
int da_count(Context *c, UInt32 *n) { return c->archive->GetNumberOfItems(n); }
int da_property(Context *c, UInt32 index, UInt32 prop, int volume, UInt32 *type, UInt64 *number, const char **text) {
  PROPVARIANT v = {}; int hr = volume ? c->archive->GetArchiveProperty(prop, &v) : c->archive->GetProperty(index, prop, &v);
  *type = v.vt; *number = 0; *text = nullptr;
  if (!hr) switch (v.vt) {
    case VT_EMPTY: break;
    case VT_BSTR: utf8(c->text, v.bstrVal); *text = c->text.c_str(); break;
    case VT_BOOL: *number = v.boolVal != 0; break;
    case VT_UI4: *number = v.ulVal; break;
    case VT_UI8: *number = v.uhVal.QuadPart; break;
    case VT_I8: *number = v.hVal.QuadPart; break;
    case VT_FILETIME: *number = ((UInt64)v.filetime.dwHighDateTime << 32) | v.filetime.dwLowDateTime; break;
    default: hr = E_NOTIMPL;
  }
  VariantClear(&v); return hr;
}
int da_parent(Context *c, UInt32 index, UInt32 *parent, UInt32 *type) {
  *parent = (UInt32)-1; *type = 0; IArchiveGetRawProps *raw = nullptr;
  int hr = c->archive->QueryInterface(IID_IArchiveGetRawProps, (void **)&raw);
  if (hr == E_NOINTERFACE) return 0; if (hr) return hr;
  hr = raw->GetParent(index, parent, type); raw->Release(); return hr;
}
// S_FALSE means use decoded extraction; only a real IInStream is exposed.
int da_item_open(Context *c, UInt32 index, Item **out) {
  *out = nullptr; IInArchiveGetStream *get = nullptr;
  int hr = c->archive->QueryInterface(IID_IInArchiveGetStream, (void **)&get);
  if (hr == E_NOINTERFACE) return 1; if (hr) return hr;
  ISequentialInStream *seq = nullptr; hr = get->GetStream(index, &seq); get->Release();
  if (hr) { if (seq) seq->Release(); return hr; } if (!seq) return 1;
  IInStream *stream = nullptr; hr = seq->QueryInterface(IID_IInStream, (void **)&stream); seq->Release();
  if (hr == E_NOINTERFACE) return 1; if (hr) return hr;
  *out = new Item{c, stream, c->input->position}; return 0;
}
int da_item_read(Item *i, void *buffer, UInt32 size, UInt32 *done) {
  i->context->input->position = i->sourcePosition;
  int hr = i->stream->Read(buffer, size, done); i->sourcePosition = i->context->input->position; return hr;
}
int da_item_seek(Item *i, Int64 offset, UInt32 origin, UInt64 *position) {
  i->context->input->position = i->sourcePosition;
  int hr = i->stream->Seek(offset, origin, position); i->sourcePosition = i->context->input->position; return hr;
}
void da_item_close(Item *i) { if (i) { i->stream->Release(); delete i; } }
int da_extract(Context *c, UInt32 index, void *user, WriteFn write, Int32 *result) {
  auto output = new Output(user, write); auto callback = new Extract(index, output);
  int hr = c->archive->Extract(&index, 1, 0, callback);
  *result = callback->requested ? callback->result : -1; callback->Release(); output->Release(); return hr;
}
}

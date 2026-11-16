/* QA #156 pass 2: make this process report another machine name (gethostname and uname). */
#include <string.h>
#include <stdlib.h>
#include <sys/utsname.h>
#include <dlfcn.h>
static const char *fake(void) { const char *h = getenv("FAKE_HOSTNAME"); return h ? h : "otherhost"; }
int gethostname(char *name, size_t len) { strncpy(name, fake(), len); if (len) name[len-1] = 0; return 0; }
int uname(struct utsname *buf) {
    static int (*real)(struct utsname *) = 0;
    if (!real) real = (int (*)(struct utsname *))dlsym(RTLD_NEXT, "uname");
    int r = real(buf);
    if (r == 0) { strncpy(buf->nodename, fake(), sizeof(buf->nodename)); buf->nodename[sizeof(buf->nodename)-1] = 0; }
    return r;
}

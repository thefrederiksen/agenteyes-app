/* Issue #156 pass 2 proof only: make this process report another machine name. */
#include <string.h>
#include <stdlib.h>
#include <sys/utsname.h>
#include <dlfcn.h>
int gethostname(char *name, size_t len) {
    const char *h = getenv("FAKE_HOSTNAME");
    if (!h) h = "otherhost";
    strncpy(name, h, len); if (len) name[len-1] = 0; return 0;
}
int uname(struct utsname *buf) {
    static int (*real)(struct utsname *) = 0;
    if (!real) real = (int (*)(struct utsname *))dlsym(RTLD_NEXT, "uname");
    int r = real(buf);
    const char *h = getenv("FAKE_HOSTNAME"); if (!h) h = "otherhost";
    if (r == 0) { strncpy(buf->nodename, h, sizeof(buf->nodename)); buf->nodename[sizeof(buf->nodename)-1]=0; }
    return r;
}

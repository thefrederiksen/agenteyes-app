// QA #156 pass 4 (Linux seat): a Landlock confinement that refuses READS of OTHER processes'
// /proc/<pid> entries and nothing else. Every other path stays readable; writes and signals are
// not handled at all (kernel 6.8 = Landlock ABI 4 has no signal scoping, so kill(pid, 0) still
// succeeds). This is the Linux analogue of the Review Gate's macOS Seatbelt profile: a prober
// that is refused the start time of a LIVE process of the SAME user while kill(pid, 0) succeeds.
//
//   llprobe -- <cmd> [args...]   confine, then exec <cmd> (same pid, so its own /proc stays readable)
//   ll_restrict_thread()         (built with -DLL_LIB as libllprobe.so) confine the CALLING thread only
#define _GNU_SOURCE
#include <linux/landlock.h>
#include <sys/syscall.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <dirent.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <ctype.h>
#include <errno.h>
#include <stddef.h>
#include <linux/filter.h>
#include <linux/seccomp.h>
#include <linux/audit.h>

// --kill-eperm: a seccomp filter that answers kill(2) with EPERM (and nothing else), the errno a
// signal-scoping sandbox returns (Landlock ABI 6 LANDLOCK_SCOPE_SIGNAL, Linux 6.12+). x86_64 only.
static int deny_kill_eperm(void) {
    struct sock_filter f[] = {
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, arch)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, AUDIT_ARCH_X86_64, 1, 0),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW),
        BPF_STMT(BPF_LD | BPF_W | BPF_ABS, offsetof(struct seccomp_data, nr)),
        BPF_JUMP(BPF_JMP | BPF_JEQ | BPF_K, __NR_kill, 0, 1),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | (EPERM & SECCOMP_RET_DATA)),
        BPF_STMT(BPF_RET | BPF_K, SECCOMP_RET_ALLOW),
    };
    struct sock_fprog prog = { .len = sizeof f / sizeof f[0], .filter = f };
    if (prctl(PR_SET_SECCOMP, SECCOMP_MODE_FILTER, &prog)) { perror("llprobe: seccomp"); return -1; }
    fprintf(stderr, "llprobe: seccomp: kill(2) answers EPERM\n");
    return 0;
}

#define RD (LANDLOCK_ACCESS_FS_READ_FILE | LANDLOCK_ACCESS_FS_READ_DIR)

static int add(int rs, const char *path) {
    int fd = open(path, O_PATH | O_CLOEXEC);
    if (fd < 0) return 0;  // vanished between readdir and open (a /proc entry): nothing to allow
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return 0; }
    struct landlock_path_beneath_attr pb = {
        .allowed_access = S_ISDIR(st.st_mode) ? RD : LANDLOCK_ACCESS_FS_READ_FILE,
        .parent_fd = fd };
    int rc = syscall(SYS_landlock_add_rule, rs, LANDLOCK_RULE_PATH_BENEATH, &pb, 0);
    close(fd);
    if (rc != 0) { fprintf(stderr, "llprobe: add_rule %s: %s\n", path, strerror(errno)); return -1; }
    return 0;
}

static int numeric(const char *s) { if (!*s) return 0; for (; *s; s++) if (!isdigit((unsigned char)*s)) return 0; return 1; }

int ll_restrict_thread(void) {
    struct landlock_ruleset_attr ra = { .handled_access_fs = RD };
    int rs = syscall(SYS_landlock_create_ruleset, &ra, sizeof ra, 0);
    if (rs < 0) { perror("llprobe: landlock_create_ruleset"); return -1; }
    char p[4096], me[32];
    snprintf(me, sizeof me, "%d", (int)getpid());
    DIR *d = opendir("/");
    struct dirent *e;
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..") || !strcmp(e->d_name, "proc")) continue;
        snprintf(p, sizeof p, "/%s", e->d_name);
        if (add(rs, p)) return -1;
    }
    closedir(d);
    d = opendir("/proc");
    int allowed = 0, denied = 0;
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        if (numeric(e->d_name) && strcmp(e->d_name, me)) { denied++; continue; }  // another process: refused
        snprintf(p, sizeof p, "/proc/%s", e->d_name);
        if (add(rs, p)) return -1;
        allowed++;
    }
    closedir(d);
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0)) { perror("llprobe: no_new_privs"); return -1; }
    if (syscall(SYS_landlock_restrict_self, rs, 0)) { perror("llprobe: landlock_restrict_self"); return -1; }
    close(rs);
    fprintf(stderr, "llprobe: Landlock ABI %ld confined pid %s thread %ld: reads of %d other /proc/<pid> entries refused, %d /proc entries (own pid + non-pid) allowed\n",
            syscall(SYS_landlock_create_ruleset, NULL, 0, LANDLOCK_CREATE_RULESET_VERSION), me, (long)syscall(SYS_gettid), denied, allowed);
    return 0;
}

#ifndef LL_LIB
int main(int argc, char **argv) {
    int i = 1;
    int ke = 0;
    if (i < argc && !strcmp(argv[i], "--kill-eperm")) { ke = 1; i++; }
    if (i < argc && !strcmp(argv[i], "--")) i++;
    if (i >= argc) { fprintf(stderr, "usage: llprobe -- cmd [args]\n"); return 2; }
    if (ll_restrict_thread()) return 3;
    if (ke && deny_kill_eperm()) return 3;   // after no_new_privs, which ll_restrict_thread set
    execvp(argv[i], argv + i);
    perror("llprobe: exec");
    return 4;
}
#endif

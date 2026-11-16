// QA #161 Linux seat: a Landlock DENY-LIST built from an allow-list.
//
//   lldeny <deny-path> [<deny-path> ...] -- <cmd> [args...]
//
// Landlock only grants; it cannot deny a subtree of an allowed tree. So this walks from "/" down
// to every deny path and, at each ancestor, grants FULL access to every sibling EXCEPT the child
// that leads to (or is) a deny path. The deny paths themselves, and every ancestor directory
// itself (not its other children), are left with NO grant: open/read/readdir/create/write/unlink
// under a deny path is refused by the kernel with EACCES. stat() is not a Landlock right, so a
// metadata lookup of a path name is NOT refused - stated, not hidden.
// Then it execs <cmd> (same pid), so the confinement covers the process and every child.
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
#include <errno.h>

#define MAXD 16
static char *deny[MAXD];
static int ndeny;
static __u64 dir_rights, file_rights;

static int is_prefix_dir(const char *a, const char *b) {  // a == b or a is an ancestor of b
    size_t n = strlen(a);
    if (strncmp(a, b, n) != 0) return 0;
    return b[n] == 0 || b[n] == '/' || (n == 1 && a[0] == '/');
}

static int add(int rs, const char *path) {
    int fd = open(path, O_PATH | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) return 0;
    struct stat st;
    if (fstat(fd, &st) != 0) { close(fd); return 0; }
    if (S_ISLNK(st.st_mode)) {  // grant the link's target object
        close(fd);
        fd = open(path, O_PATH | O_CLOEXEC);
        if (fd < 0) return 0;
        if (fstat(fd, &st) != 0) { close(fd); return 0; }
    }
    struct landlock_path_beneath_attr pb = {
        .allowed_access = S_ISDIR(st.st_mode) ? dir_rights : file_rights, .parent_fd = fd };
    int rc = syscall(SYS_landlock_add_rule, rs, LANDLOCK_RULE_PATH_BENEATH, &pb, 0);
    close(fd);
    if (rc != 0) { fprintf(stderr, "lldeny: add_rule %s: %s\n", path, strerror(errno)); return -1; }
    return 0;
}

// Grant every child of dir except the ones that are, or lead to, a deny path; recurse into the latter.
static int walk(int rs, const char *dir) {
    DIR *d = opendir(dir);
    if (!d) { fprintf(stderr, "lldeny: opendir %s: %s\n", dir, strerror(errno)); return -1; }
    struct dirent *e;
    while ((e = readdir(d))) {
        if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
        char p[4096];
        snprintf(p, sizeof p, "%s%s%s", dir, strcmp(dir, "/") ? "/" : "", e->d_name);
        int on_path = 0, is_deny = 0;
        for (int i = 0; i < ndeny; i++) {
            if (!strcmp(p, deny[i])) is_deny = 1;
            else if (is_prefix_dir(p, deny[i])) on_path = 1;
        }
        if (is_deny) { fprintf(stderr, "lldeny: DENY %s\n", p); continue; }
        if (on_path) { if (walk(rs, p)) { closedir(d); return -1; } continue; }
        if (add(rs, p)) { closedir(d); return -1; }
    }
    closedir(d);
    return 0;
}

int main(int argc, char **argv) {
    int i = 1;
    for (; i < argc && strcmp(argv[i], "--"); i++) {
        if (ndeny == MAXD || argv[i][0] != '/') { fprintf(stderr, "lldeny: bad deny path %s\n", argv[i]); return 2; }
        deny[ndeny++] = argv[i];
    }
    if (i >= argc - 1 || ndeny == 0) { fprintf(stderr, "usage: lldeny <deny>... -- cmd...\n"); return 2; }
    int abi = syscall(SYS_landlock_create_ruleset, NULL, 0, LANDLOCK_CREATE_RULESET_VERSION);
    if (abi < 1) { perror("lldeny: landlock unavailable"); return 3; }
    file_rights = LANDLOCK_ACCESS_FS_EXECUTE | LANDLOCK_ACCESS_FS_WRITE_FILE | LANDLOCK_ACCESS_FS_READ_FILE;
    if (abi >= 3) file_rights |= LANDLOCK_ACCESS_FS_TRUNCATE;
    dir_rights = file_rights | LANDLOCK_ACCESS_FS_READ_DIR | LANDLOCK_ACCESS_FS_REMOVE_DIR |
        LANDLOCK_ACCESS_FS_REMOVE_FILE | LANDLOCK_ACCESS_FS_MAKE_CHAR | LANDLOCK_ACCESS_FS_MAKE_DIR |
        LANDLOCK_ACCESS_FS_MAKE_REG | LANDLOCK_ACCESS_FS_MAKE_SOCK | LANDLOCK_ACCESS_FS_MAKE_FIFO |
        LANDLOCK_ACCESS_FS_MAKE_BLOCK | LANDLOCK_ACCESS_FS_MAKE_SYM;
    if (abi >= 2) dir_rights |= LANDLOCK_ACCESS_FS_REFER;
    struct landlock_ruleset_attr ra = { .handled_access_fs = dir_rights };
    int rs = syscall(SYS_landlock_create_ruleset, &ra, sizeof ra, 0);
    if (rs < 0) { perror("lldeny: create_ruleset"); return 3; }
    if (walk(rs, "/")) return 3;
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0)) { perror("lldeny: no_new_privs"); return 3; }
    if (syscall(SYS_landlock_restrict_self, rs, 0)) { perror("lldeny: restrict_self"); return 3; }
    fprintf(stderr, "lldeny: landlock ABI %d enforced, %d deny path(s)\n", abi, ndeny);
    execvp(argv[i + 1], argv + i + 1);
    perror("lldeny: exec");
    return 4;
}

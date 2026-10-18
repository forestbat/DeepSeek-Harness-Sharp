import { readdir, readlink, stat } from "node:fs/promises";

const roots = new Set(Bun.argv.slice(2));
if (roots.size === 0)
    throw new Error("Pass the exact owned TMPDIR paths as arguments.");
const processes = [];
const socketOwners = new Map();
for (const name of await readdir("/proc")) {
    if (!/^\d+$/.test(name) || [process.pid, process.ppid].includes(Number(name)))
        continue;
    const directory = `/proc/${name}`;
    try {
        if ((await stat(directory)).uid !== process.getuid())
            continue;
        const environment = (await Bun.file(`${directory}/environ`).text()).split("\0");
        if (![...roots].some(root => environment.includes(`TMPDIR=${root}`)))
            continue;
        const status = await Bun.file(`${directory}/status`).text();
        const parentPid = Number(status.match(/^PPid:\s+(\d+)/m)[1]);
        if (parentPid === process.ppid)
            continue;
        const pid = Number(name);
        processes.push({
            pid,
            parentPid,
            executable: (await Bun.file(`${directory}/comm`).text()).trim(),
        });
        for (const descriptor of await readdir(`${directory}/fd`)) {
            try {
                const socket = (await readlink(`${directory}/fd/${descriptor}`)).match(/^socket:\[(\d+)\]$/);
                if (socket)
                    socketOwners.set(socket[1], [...(socketOwners.get(socket[1]) ?? []), pid]);
            } catch (error) {
                if (!["ENOENT", "EACCES", "ESRCH"].includes(error.code))
                    throw error;
            }
        }
    } catch (error) {
        if (!["ENOENT", "EACCES", "ESRCH"].includes(error.code))
            throw error;
    }
}

const sockets = [];
for (const protocol of ["tcp", "tcp6"]) {
    const lines = (await Bun.file(`/proc/net/${protocol}`).text()).trimEnd().split("\n").slice(1);
    for (const line of lines) {
        const columns = line.trim().split(/\s+/);
        if (columns[3] === "0A" && socketOwners.has(columns[9]))
            sockets.push({
                protocol,
                port: Number.parseInt(columns[1].split(":")[1], 16),
                pids: socketOwners.get(columns[9]),
            });
    }
}
for (const line of (await Bun.file("/proc/net/unix").text()).trimEnd().split("\n").slice(1)) {
    const columns = line.trim().split(/\s+/);
    if (socketOwners.has(columns[6]))
        sockets.push({
            protocol: "unix",
            path: columns.slice(7).join(" "),
            pids: socketOwners.get(columns[6]),
        });
}
console.log(JSON.stringify({
    ownership: "Exact inherited TMPDIR match; environment values and command arguments are not emitted.",
    processes: processes.sort((left, right) => left.pid - right.pid),
    sockets,
}, null, 2));

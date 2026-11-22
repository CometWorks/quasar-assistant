import { client } from "./_sdk/client.js";

const listeners = new Set();
const topics = {status: 1, index: 2, chat: 2 | 4, approvals: 4, monitor: 8};
let subscription;

async function receive(signal) {
    while (!signal.aborted) {
        try {
            for await (const changed of client.subscribe("quasar.assistant", "panel.watch", {}, {signal})) {
                await Promise.all([...listeners].filter(l => changed & topics[l.topic]).map(l => l.notify()));
            }
        } catch (error) {
            if (signal.aborted) return;
            await Promise.all([...listeners].map(l => l.error(error.message)));
            if ([401, 403].includes(error.status)) return;
        }
        if (!signal.aborted) await new Promise(resolve => setTimeout(resolve, 1500));
    }
}

export function open() {
    const lifetime = new AbortController();
    let listener;
    function release() {
        listeners.delete(listener);
        if (!listeners.size) {
            subscription?.abort();
            subscription = undefined;
        }
    }
    return {
        async request(operation, json) {
            const result = await client.request("quasar.assistant", operation, JSON.parse(json), {signal: lifetime.signal});
            return JSON.stringify(result ?? null);
        },
        async uploadFile(operation, json, input) {
            const file = input.files[0];
            if (!file) throw new Error("Select a file.");
            const upload = await client.openWrite("quasar.assistant", operation, JSON.parse(json), { signal: lifetime.signal });
            await upload.write(file);
        },
        async upload(operation, json) {
            const upload = await client.openWrite("quasar.assistant", operation, {}, { signal: lifetime.signal });
            return JSON.stringify(await upload.write(new Blob([json], { type: "application/json" })));
        },
        watch(topic, view) {
            async function invoke(method, ...args) {
                if (!lifetime.signal.aborted) {
                    try { await view.invokeMethodAsync(method, ...args); }
                    catch (error) { if (!lifetime.signal.aborted) throw error; }
                }
            }
            if (listener) listeners.delete(listener);
            listener = { topic, notify: () => invoke("FeedChanged"), error: message => invoke("Failed", message) };
            listeners.add(listener);
            if (!subscription) {
                subscription = new AbortController();
                void receive(subscription.signal);
            } else {
                void listener.notify();
            }
        },
        close() { lifetime.abort(); release(); }
    };
}

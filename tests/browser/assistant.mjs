import assert from "node:assert/strict";
import { createServer } from "node:http";
import { once } from "node:events";

// QUASAR_TEST_PLUGIN_DRIVER for the independently published Quasar dashboard checks.
export async function checkPlugin(page, base) {
    const circuits = [];
    const recordCircuit = request => {
        if (new URL(request.url()).pathname.split("/").includes("_blazor")) circuits.push(request.url());
    };
    page.on("request", recordCircuit);
    page.on("websocket", recordCircuit);
    try {
        await checkWorkflows(page, base);
        const direct = base.endsWith("/fixture-panel/") && process.env.QUASAR_RTC_TEST_PEER;
        const transport = await page.evaluate(async () => {
            const {client} = await import(new URL("_quasar/plugins/quasar.assistant/_sdk/client.js", document.baseURI));
            const before = client.transport.receivedBytes;
            await client.request("quasar.assistant", "panel.status", {});
            return {path: client.transport.path, received: client.transport.receivedBytes - before};
        });
        assert.equal(transport.path, direct ? "direct" : "http");
        if (direct) assert.ok(transport.received > 0);
        assert.deepEqual(circuits, []);
        console.log(`Assistant workflows passed in ${page.context().browser().browserType().name()} over ${transport.path} at ${base}`);
    } finally {
        page.off("request", recordCircuit);
        page.off("websocket", recordCircuit);
    }
}

async function checkWorkflows(page, base) {
    await page.addLocatorHandler(page.getByRole("dialog").filter({hasText:"Set up administrator access"}), async dialog => {
        await dialog.getByRole("button",{name:"Cancel",exact:true}).click();
    });
    const goto = path => page.goto(base + "assistant/" + path);
    await goto("approvals");
    await page.getByText("No player is waiting.").waitFor();
    await goto("monitor");
    await page.getByText("No open problem.").waitFor();
    await goto("lore");
    const target = "fixture-" + Date.now();
    await page.getByLabel(/Another server or cluster/).fill(target);
    await page.getByRole("button", {name:"Add",exact:true}).click();
    await page.locator("textarea").fill("Fixture world lore");
    await page.getByRole("button",{name:"Save",exact:true}).click();
    await page.getByRole("rowheader",{name:target}).waitFor();
    await goto("settings");
    const limit = page.getByLabel("Delete conversations after (days)");
    await limit.waitFor();
    const old = await limit.inputValue();
    await limit.fill(String(Number(old)+1));
    await limit.press("Tab");
    await page.getByText("Saved.",{exact:true}).waitFor();
    await page.getByRole("tab",{name:"Admin assistant"}).click();
    await page.getByRole("button",{name:"Add a skill",exact:true}).waitFor();
    await page.getByRole("tab",{name:"Player support"}).click();
    await page.getByRole("heading",{name:"Grid restores"}).waitFor();
    await page.getByRole("button",{name:"Upload a SKILL.md"}).click();
    await page.getByLabel(/Source name/).fill("browser-source");
    await page.locator('input[type="file"]').setInputFiles({
        name:"SKILL.md",mimeType:"text/markdown",
        buffer:Buffer.from("---\nname: uploaded-faq\ndescription: Fixture skill\n---\n" + "fixture ".repeat(10000))
    });
    await page.getByRole("button",{name:"Save",exact:true}).click();
    await page.getByText(/Saved browser-source/).waitFor();
    await page.getByRole("tab",{name:"Monitor"}).click();
    await page.getByRole("heading",{name:"Token budget"}).waitFor();
    await page.getByRole("button",{name:"Revert my changes"}).click();
    await page.getByRole("button",{name:"Yes",exact:true}).click();
    await page.getByRole("tab",{name:"Providers"}).click();
    assert.equal(await limit.inputValue(),old);
    await checkChat(page, base);
}

// A worker-local streaming provider. The browser observes a partial answer before the model completes.
async function checkChat(page, base) {
    let release;
    const finish = new Promise(resolve => { release = resolve; });
    let authorized = false;
    const server = createServer(async (req,res) => {
        let input = "";
        for await (const chunk of req) input += chunk;
        const {messages,tools} = JSON.parse(input);
        authorized = req.headers.authorization === "Bearer fixture-model-key"
            && req.headers["x-fixture"] === "fixture-header-value";
        res.writeHead(200, {"Content-Type":"text/event-stream"});
        const event = (delta, reason = null) => res.write("data: " + JSON.stringify({
            choices: [{index:0,delta,finish_reason:reason}]
        }) + "\n\n");
        if (messages.at(-1)?.content?.includes("Please propose")) {
            const tool = tools.find(t => t.function.name.endsWith("__players_kick"));
            if (tool) {
                event({tool_calls:[{index:0,id:"fixture-call",type:"function",function:{
                    name:tool.function.name, arguments:JSON.stringify({server:"fixture-offline",steamId:"76561198000000001"})
                }}]});
                event({},"tool_calls");
            } else event({content:"No kick tool was offered."},"stop");
        } else if (messages.at(-1)?.content?.includes("Stay busy")) {
            event({content:"Cancellable reply"});
            await once(res,"close");
            return;
        } else {
            event({content:"Streaming fixture"});
            await finish;
            event({content:" completed."},"stop");
        }
        res.end("data: [DONE]\n\n");
    });
    server.listen(0,"127.0.0.1");
    await once(server,"listening");
    const endpoint = "http://127.0.0.1:" + server.address().port + "/v1";
    const sdk = (operation, input) => page.evaluate(async ({operation,input}) => {
        const {client} = await import(new URL("_quasar/plugins/quasar.assistant/_sdk/client.js",document.baseURI));
        return client.request("quasar.assistant",operation,input);
    }, {operation,input});
    let snapshot;
    try {
        snapshot = await sdk("settings.snapshot",{});
        const before = (await sdk("settings.panel",{})).settings;
        const backend = {name:"fixture",kind:"OpenAi",model:"fixture",baseUrl:endpoint,headers:{"X-Fixture":"fixture-header-value"}};
        const after = {...before, backends:[backend], roles:{
            ...before.roles, admin:{...before.roles.admin, backends:["fixture"]}
        }};
        await page.evaluate(async change => {
            const {client} = await import(new URL("_quasar/plugins/quasar.assistant/_sdk/client.js", document.baseURI));
            const upload = await client.openWrite("quasar.assistant","settings.save",{});
            await upload.write(new Blob([JSON.stringify(change)], {type:"application/json"}));
        }, {before, after, backendName:"fixture", apiKey:"fixture-model-key", headerChanges:[], replaceBackendHeaders:true});
        const settings = JSON.stringify(await sdk("settings.panel",{}));
        assert.doesNotMatch(settings,/fixture-model-key|fixture-header-value/);
        await page.goto(base + "assistant/monitor");
        await page.getByRole("button",{name:"Open the assistant"}).click();
        const drawer = page.getByRole("complementary",{name:"Assistant",exact:true});
        await drawer.getByLabel("Message to the assistant").fill("Hello from the drawer");
        await drawer.getByRole("button",{name:"Send",exact:true}).click();
        await drawer.getByText("Streaming fixture",{exact:true}).waitFor();
        await drawer.getByRole("button",{name:"Stop",exact:true}).waitFor();
        release();
        await drawer.getByText("Streaming fixture completed.",{exact:true}).waitFor();
        assert.ok(authorized, "the worker used its stored model key and custom header");
        await drawer.getByRole("link",{name:"Open on the Assistant page"}).click();
        await page.waitForURL(/assistant\/chat\/[a-f0-9]{32}$/);
        await page.reload();
        await page.locator(".assistant-message-user").getByText("Hello from the drawer").waitFor();
        await page.getByLabel("Message to the assistant").fill("Stay busy");
        await page.getByRole("button",{name:"Send",exact:true}).click();
        await page.getByText("Cancellable reply",{exact:true}).waitFor();
        await page.getByRole("button",{name:"Stop",exact:true}).click();
        await page.getByText("Stopped.",{exact:true}).waitFor();
        await page.getByLabel("Message to the assistant").fill("Please propose the fixture change");
        await page.getByRole("button",{name:"Send",exact:true}).click();
        await page.getByRole("button",{name:"Reject",exact:true}).waitFor();
        await page.goto(base + "assistant/approvals");
        const card = page.locator(".assistant-proposal").filter({has:page.getByRole("button",{name:"Reject",exact:true})}).first();
        await card.getByLabel("Reason for rejecting").fill("Browser fixture");
        await card.getByRole("button",{name:"Reject",exact:true}).click();
        await page.getByText(/Rejected by/).first().waitFor();
    } finally {
        release();
        try {
            if (snapshot) await sdk("settings.revert",{id:snapshot});
        } finally {
            server.closeAllConnections();
            server.close();
        }
    }
}

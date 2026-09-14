export function download(content) {
    const url = URL.createObjectURL(new Blob([content], { type: 'text/x-shellscript;charset=utf-8' }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = 'deploy.parameters.sh';
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function initializeTabs(tabList) {
    tabList.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        const tabs = Array.from(tabList.querySelectorAll('[role="tab"]:not(:disabled)'));
        const current = tabs.indexOf(event.target);
        if (current < 0) return;
        event.preventDefault();
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
            : (current + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
        tabs[next].focus();
        tabs[next].click();
        tabs[next].scrollIntoView({ block: 'nearest', inline: 'nearest' });
    });
}

export function downloadArm(content, filename = 'azuredeploy.json') {
    const type = filename.endsWith('.sh') ? 'text/x-shellscript;charset=utf-8' : 'application/json;charset=utf-8';
    const url = URL.createObjectURL(new Blob([content], { type }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
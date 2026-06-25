window.chatScroll = {
    register: function (el) {
        if (!el || el._chatScrollRegistered) {
            return;
        }
        el._chatScrollRegistered = true;
        el._stick = true;
        el.addEventListener('scroll', function () {
            const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
            el._stick = atBottom;
        });
    },
    scrollIfStuck: function (el) {
        if (!el) {
            return;
        }
        if (el._stick !== false) {
            el.scrollTop = el.scrollHeight;
        }
    }
};

window.chatComposer = {
    register: function (el, dotNetRef) {
        if (!el || el._chatComposerRegistered) {
            return;
        }

        el._chatComposerRegistered = true;
        el.addEventListener('keydown', function (event) {
            if (event.key !== 'Enter' || event.shiftKey) {
                return;
            }

            event.preventDefault();
            dotNetRef.invokeMethodAsync('SubmitComposerAsync');
        });
    }
};

window.probeTable = {
    _current: null,
    register: function (tableEl) {
        if (!tableEl || tableEl._probeTableRegistered) {
            return;
        }
        tableEl._probeTableRegistered = true;
        const self = this;

        tableEl.addEventListener('mouseover', function (e) {
            const row = e.target.closest('tr.result-row');
            if (!row || !tableEl.contains(row)) {
                return;
            }
            if (row === self._current) {
                return;
            }

            // hide previous panel
            if (self._current) {
                const prev = self._current.querySelector('.probe-summary-panel');
                if (prev) {
                    prev.style.display = 'none';
                }
            }
            self._current = row;

            const panel = row.querySelector('.probe-summary-panel');
            if (!panel) {
                return;
            }

            const rowRect = row.getBoundingClientRect();
            const vh = window.innerHeight;
            const vw = window.innerWidth;
            const panelW = 700;
            const panelMaxH = 460;

            // horizontal: align to right edge of row, clamped inside viewport
            let left = Math.round(rowRect.right) - panelW;
            left = Math.max(8, Math.min(left, vw - panelW - 8));

            // vertical: show below when there is room, above otherwise
            const spaceBelow = vh - rowRect.bottom - 8;
            const spaceAbove = rowRect.top - 8;
            let top;
            if (spaceBelow >= 180 || spaceBelow >= spaceAbove) {
                top = Math.round(rowRect.bottom) + 2;
            } else {
                const clampedH = Math.min(panelMaxH, spaceAbove);
                top = Math.round(rowRect.top) - clampedH - 2;
            }

            panel.style.cssText =
                'display:block; position:fixed; left:' + left + 'px; top:' + top + 'px;' +
                ' width:' + panelW + 'px; max-height:' + panelMaxH + 'px;';
        });

        tableEl.addEventListener('mouseleave', function () {
            if (self._current) {
                const panel = self._current.querySelector('.probe-summary-panel');
                if (panel) {
                    panel.style.display = 'none';
                }
                self._current = null;
            }
        });
    }
};

window.chatResponseDetails = {
    register: function (root) {
        if (!root || root._chatResponseDetailsRegistered) {
            return;
        }

        root._chatResponseDetailsRegistered = true;
        const setActive = function (event) {
            const trigger = event.target.closest('.metrics-host, .headers-host, .raw-host');
            if (!trigger || !root.contains(trigger)) {
                return;
            }

            const bubble = trigger.closest('.assistant-bubble');
            if (!bubble) {
                return;
            }

            if (trigger.classList.contains('metrics-host')) {
                bubble.dataset.activeDetail = 'metrics';
                return;
            }

            bubble.dataset.activeDetail = trigger.classList.contains('headers-host') ? 'headers' : 'raw';

            const bubbleRect = bubble.getBoundingClientRect();
            const rootRect = root.getBoundingClientRect();
            const availableAbove = bubbleRect.top - rootRect.top;
            const expectedPanelHeight = Math.min(416, Math.max(220, root.clientHeight * 0.55));
            bubble.dataset.detailPlacement = availableAbove < expectedPanelHeight ? 'below' : 'above';
        };

        const clearActive = function (event) {
            if (!event.target.classList || !event.target.classList.contains('assistant-bubble')) {
                return;
            }

            if (root.contains(event.target)) {
                delete event.target.dataset.activeDetail;
            }
        };

        root.addEventListener('pointerenter', setActive, true);
        root.addEventListener('focusin', setActive, true);
        root.addEventListener('pointerleave', clearActive, true);
    }
};

window.responseSearch = (function () {
    const roots = [];
    let current = null;
    let listening = false;

    function register(root) {
        if (!root || root._responseSearchRegistered) {
            return;
        }

        root._responseSearchRegistered = true;
        root.setAttribute('tabindex', root.getAttribute('tabindex') || '0');
        root.addEventListener('mousedown', function () {
            window.setTimeout(function () {
                if (!isInteractive(document.activeElement) && document.body.contains(root)) {
                    root.focus({ preventScroll: true });
                }

                if (current && current.root !== root) {
                    switchRoot(root);
                }
            }, 0);
        });
        root.addEventListener('focusin', function () {
            if (current && current.root !== root) {
                switchRoot(root);
            }
        });
        roots.push(root);

        if (!listening) {
            document.addEventListener('keydown', onDocumentKeyDown, true);
            listening = true;
        }
    }

    function refresh() {
        if (!current) {
            return;
        }

        const query = current.query;
        if (!document.body.contains(current.root)) {
            const replacementRoot = findReplacementRoot(current);
            if (!replacementRoot) {
                return;
            }

            current.root = replacementRoot;
        }

        if (!document.body.contains(current.popup)) {
            const root = current.root;
            clearMarks(root);
            current = null;
            createPopup(root);
            current.input.value = query;
        }

        if (query) {
            current.input.value = query;
            applySearch(query);
        }
    }

    function onDocumentKeyDown(event) {
        const key = event.key.toLowerCase();
        if (!(event.ctrlKey || event.metaKey) || (key !== 'f' && key !== 's')) {
            return;
        }

        const root = findFocusedRoot();
        if (!root) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        show(root);
    }

    function findFocusedRoot() {
        const active = document.activeElement;
        for (const root of roots) {
            if (root && document.body.contains(root) && (root === active || root.contains(active))) {
                return root;
            }
        }

        return null;
    }

    function show(root) {
        if (current && current.root !== root) {
            switchRoot(root);
            return;
        }

        if (!current) {
            createPopup(root);
        }

        current.input.focus();
        current.input.select();
    }

    function createPopup(root) {
        const host = root.closest('.search-host') || root.parentElement || root;
        const popup = document.createElement('div');
        popup.className = 'response-search-popup';

        const input = document.createElement('input');
        input.className = 'form-control form-control-sm';
        input.type = 'text';
        input.placeholder = 'Find';

        const status = document.createElement('span');
        status.className = 'response-search-status';
        status.textContent = '0/0';

        const previous = document.createElement('button');
        previous.className = 'btn btn-sm btn-outline-secondary';
        previous.type = 'button';
        previous.textContent = '<';
        previous.title = 'Previous match';

        const next = document.createElement('button');
        next.className = 'btn btn-sm btn-outline-secondary';
        next.type = 'button';
        next.textContent = '>';
        next.title = 'Next match';

        const closeButton = document.createElement('button');
        closeButton.className = 'btn btn-sm btn-outline-secondary';
        closeButton.type = 'button';
        closeButton.textContent = 'x';
        closeButton.title = 'Close search';

        popup.append(input, status, previous, next, closeButton);
        host.appendChild(popup);

        current = { root, popup, input, status, marks: [], index: -1, query: '' };

        input.addEventListener('input', function () {
            applySearch(input.value);
        });
        input.addEventListener('keydown', function (event) {
            if (event.key === 'ArrowDown' || event.key === 'Enter') {
                event.preventDefault();
                move(1);
            } else if (event.key === 'ArrowUp') {
                event.preventDefault();
                move(-1);
            } else if (event.key === 'Escape') {
                event.preventDefault();
                close(true);
            }
        });
        previous.addEventListener('click', function () { move(-1); });
        next.addEventListener('click', function () { move(1); });
        closeButton.addEventListener('click', function () { close(true); });

        return popup;
    }

    function switchRoot(root) {
        const query = current ? current.query : '';
        close(false);
        createPopup(root);
        current.input.value = query;
        applySearch(query);
        current.input.focus();
        current.input.select();
    }

    function applySearch(query) {
        if (!current) {
            return;
        }

        clearMarks(current.root);
        current.marks = [];
        current.index = -1;
        current.query = query;

        if (!query) {
            updateStatus();
            return;
        }

        const escapedQuery = escapeRegExp(query);
        const matcher = new RegExp(escapedQuery, 'gi');
        const lowerQuery = query.toLowerCase();
        const walker = document.createTreeWalker(current.root, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (!node.nodeValue || !node.nodeValue.toLowerCase().includes(lowerQuery)) {
                    return NodeFilter.FILTER_REJECT;
                }
                if (current.popup.contains(node.parentElement)) {
                    return NodeFilter.FILTER_REJECT;
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });

        const nodes = [];
        while (walker.nextNode()) {
            nodes.push(walker.currentNode);
        }

        for (const node of nodes) {
            highlightNode(node, matcher);
        }

        if (current.marks.length > 0) {
            current.index = 0;
            activateCurrent();
        }

        updateStatus();
    }

    function highlightNode(node, matcher) {
        const text = node.nodeValue;
        const fragment = document.createDocumentFragment();
        let lastIndex = 0;
        matcher.lastIndex = 0;

        for (const match of text.matchAll(matcher)) {
            if (match.index > lastIndex) {
                fragment.appendChild(document.createTextNode(text.slice(lastIndex, match.index)));
            }

            const mark = document.createElement('mark');
            mark.className = 'response-search-match';
            mark.textContent = match[0];
            current.marks.push(mark);
            fragment.appendChild(mark);
            lastIndex = match.index + match[0].length;
        }

        if (lastIndex < text.length) {
            fragment.appendChild(document.createTextNode(text.slice(lastIndex)));
        }

        node.parentNode.replaceChild(fragment, node);
    }

    function move(delta) {
        if (!current || current.marks.length === 0) {
            updateStatus();
            return;
        }

        current.index = (current.index + delta + current.marks.length) % current.marks.length;
        activateCurrent();
        updateStatus();
    }

    function activateCurrent() {
        current.marks.forEach(function (mark) { mark.classList.remove('active'); });
        const active = current.marks[current.index];
        if (active) {
            active.classList.add('active');
            active.scrollIntoView({ block: 'center', inline: 'nearest' });
        }
    }

    function updateStatus() {
        if (!current) {
            return;
        }

        current.status.textContent = current.marks.length === 0 ? '0/0' : `${current.index + 1}/${current.marks.length}`;
    }

    function close(restoreFocus) {
        if (!current) {
            return;
        }

        const root = current.root;
        clearMarks(root);
        current.popup.remove();
        current = null;
        if (restoreFocus && root && document.body.contains(root)) {
            root.focus({ preventScroll: true });
        }
    }

    function findReplacementRoot(searchState) {
        const host = searchState.popup && document.body.contains(searchState.popup)
            ? searchState.popup.closest('.search-host')
            : null;
        if (!host) {
            return null;
        }

        for (let index = roots.length - 1; index >= 0; index--) {
            const root = roots[index];
            if (root && document.body.contains(root) && root.closest('.search-host') === host) {
                return root;
            }
        }

        return null;
    }

    function clearMarks(root) {
        root.querySelectorAll('mark.response-search-match').forEach(function (mark) {
            const text = document.createTextNode(mark.textContent || '');
            const parent = mark.parentNode;
            parent.replaceChild(text, mark);
            parent.normalize();
        });
    }

    function escapeRegExp(value) {
        return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }

    function isInteractive(element) {
        return element && ['INPUT', 'TEXTAREA', 'SELECT', 'BUTTON'].includes(element.tagName);
    }

    return { register, refresh };
})();

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

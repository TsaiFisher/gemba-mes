/* Capture the rendered plant once. Never replace it with another tab's current plant. */
(function () {
    'use strict';
    const plant = document.querySelector('meta[name="mes-plant-context"]')?.content || '';
    const unsafe = method => !['GET', 'HEAD', 'OPTIONS'].includes((method || 'GET').toUpperCase());
    const sameOrigin = url => new URL(url, window.location.href).origin === window.location.origin;
    const t = key => (window.i18n && typeof window.i18n.t === 'function') ? window.i18n.t(key) : key;
    const originalFetch = window.fetch;

    // 被 StalePlantContextFilter 擋下（畫面開在 A 廠、送出時已在 B 廠，409 PLANT_CONTEXT_CHANGED）時，
    // 問使用者要不要切回 A 廠並照原內容再送一次；原本只能照訊息重新整理，填的內容全部消失。
    //
    // 放在這裡而不是各畫面的送出程式：基本資料畫面（倉庫、工作中心、班別…）各自有一份 submitPanel，
    // 不走共用的 crud-panel.js。2026-11-12 只改了 crud-panel.js，11/15 重測倉庫畫面時完全沒有生效
    //（B3～B5）。這個 fetch 包裝是每一頁都會載入的唯一共同點。
    //
    // 只切回「畫面開啟時的那個廠」：資料落在使用者填表時看到的廠別。只重送一次：切回失敗或又被擋，
    // 就把原本的 409 交還給呼叫端，由它顯示訊息，不會無限重試。
    async function offerSwitchBack(response, input, init) {
        if (response.status !== 409 || (init && init.__plantRetried)) return response;
        // Request 物件的本文讀過就不能再送一次，交還呼叫端照舊處理
        if (typeof Request !== 'undefined' && input instanceof Request) return response;
        let data;
        try { data = await response.clone().json(); } catch (e) { return response; }
        if (!data || data.code !== 'PLANT_CONTEXT_CHANGED' || !data.renderedPlant) return response;

        const question = String(t('PlantContextSwitchBackConfirm')).replace('{0}', data.renderedPlant);
        if (!window.confirm((data.message || '') + '\n\n' + question)) {
            // 不切回：仍是 409，但訊息改成簡短的「沒有存檔」——完整說明剛才的確認框已經講過，
            // 呼叫端照舊顯示 message 時，不必再看一次同樣的長段落。面板與填寫內容都留著。
            return new Response(JSON.stringify(Object.assign({}, data, { message: t('PlantContextNotSaved') })),
                { status: 409, headers: { 'Content-Type': 'application/json' } });
        }

        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        const body = new URLSearchParams();
        body.set('plantId', data.renderedPlant);
        body.set('__RequestVerificationToken', token);
        try {
            const switched = await originalFetch.call(window, '/UserSettings/SwitchPlant', {
                method: 'POST', body: body,
                headers: { 'X-Requested-With': 'XMLHttpRequest', 'RequestVerificationToken': token }
            });
            if (!switched.ok) return response;
        } catch (e) {
            return response;
        }
        return window.fetch(input, Object.assign({}, init, { __plantRetried: true }));
    }

    window.fetch = function (input, init) {
        const isRequest = typeof Request !== 'undefined' && input instanceof Request;
        const url = isRequest ? input.url : input;
        if (!(sameOrigin(url) && unsafe(init?.method || (isRequest ? input.method : 'GET'))))
            return originalFetch.call(this, input, init);

        const headers = new Headers(init?.headers || (isRequest ? input.headers : undefined));
        if (!headers.has('X-Plant-Context')) headers.set('X-Plant-Context', plant);
        const stamped = Object.assign({}, init, { headers });
        return Promise.resolve(originalFetch.call(this, input, stamped))
            .then(res => (res && typeof res.status === 'number') ? offerSwitchBack(res, input, init) : res);
    };
    function stamp(form) {
        if (!unsafe(form.method) || !sameOrigin(form.action)) return;
        let field = form.querySelector('input[name="__PlantContext"]');
        if (!field) {
            field = document.createElement('input');
            field.type = 'hidden'; field.name = '__PlantContext';
            form.appendChild(field);
        }
        field.value = plant;
    }
    document.querySelectorAll('form').forEach(stamp);
    document.addEventListener('submit', event => {
        if (event.target instanceof HTMLFormElement) stamp(event.target);
    }, true);
    if (window.jQuery) window.jQuery.ajaxPrefilter(function (options, original, xhr) {
        if (sameOrigin(options.url) && unsafe(options.type)) xhr.setRequestHeader('X-Plant-Context', plant);
    });
})();

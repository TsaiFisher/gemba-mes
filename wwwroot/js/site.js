// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

document.addEventListener('DOMContentLoaded', function () {
    const sidebarToggle = document.getElementById('sidebarToggle');
    const sidebar = document.getElementById('sidebar-container');

    if (sidebarToggle && sidebar) {
        sidebarToggle.addEventListener('click', function () {
            sidebar.classList.toggle('collapsed');
        });
    }

    // 初始化 Select2
    $('.select2-enable').each(function () {
        $(this).select2({
            theme: 'bootstrap-5',
            width: $(this).data('width') ? $(this).data('width') : $(this).hasClass('w-100') ? '100%' : 'style',
            placeholder: $(this).data('placeholder'),
            allowClear: Boolean($(this).data('allow-clear'))
        });
    });

    // 進度條寬度套用（取代 inline style="width:N%"，CSP style-src 已移除 unsafe-inline；
    // CSSOM 屬性設值 el.style.width= 不受 style-src 管制）。
    document.querySelectorAll('[data-pct-width]').forEach(function (el) {
        el.style.width = el.getAttribute('data-pct-width') + '%';
    });
});

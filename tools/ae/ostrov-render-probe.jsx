// Диагностика для этапа 5в (НЕ рабочий скрипт). Раз в секунду пишет в лог состояние очереди рендеринга After Effects,
// чтобы понять: (1) выполняются ли задачи скрипта ВО ВРЕМЯ рендера, (2) какие данные о ходе рендера доступны.
//
// Лог: %AppData%\Ostrov\ae-probe.log (папку создаёт сам).
// Запуск: в AE меню File > Scripts > Run Script File... и выбрать этот файл (права администратора не нужны).
// Нужна настройка AE: Edit > Preferences > Scripting & Expressions > "Allow Scripts to Write Files and Access Network".
// Остановка: закрыть AE (или запустить скрипт ещё раз: старая задача отменяется).
//
// Первая версия без #targetengine писала в лог только пару строк и замолкала: без именованной среды скрипт живёт
// только до своего конца, и задача на повтор погибает вместе с ним. Окно alert в конце тоже блокировало задачи.

#targetengine "ostrovProbe"

(function () {
    function pad(n, len) {
        var s = String(n);
        while (s.length < (len || 2)) s = "0" + s;
        return s;
    }

    function stamp(d) {
        return pad(d.getHours()) + ":" + pad(d.getMinutes()) + ":" + pad(d.getSeconds()) + "." + pad(d.getMilliseconds(), 3);
    }

    function statusName(s) {
        try {
            if (s === RQItemStatus.WILL_CONTINUE) return "WILL_CONTINUE";
            if (s === RQItemStatus.NEEDS_OUTPUT) return "NEEDS_OUTPUT";
            if (s === RQItemStatus.UNQUEUED) return "UNQUEUED";
            if (s === RQItemStatus.QUEUED) return "QUEUED";
            if (s === RQItemStatus.RENDERING) return "RENDERING";
            if (s === RQItemStatus.USER_STOPPED) return "USER_STOPPED";
            if (s === RQItemStatus.ERR_STOPPED) return "ERR_STOPPED";
            if (s === RQItemStatus.DONE) return "DONE";
        } catch (e) { }
        return "?" + s;
    }

    function log(text) {
        try {
            var folder = new Folder(Folder.userData.fsName + "/Ostrov");
            if (!folder.exists) folder.create();
            var f = new File(folder.fsName + "/ae-probe.log");
            f.encoding = "UTF-8";
            if (f.open("a")) {
                f.writeln(text);
                f.close();
                return true;
            }
        } catch (e) { }
        return false;
    }

    $.global.ostrovProbeTick = function () {
        var now = new Date();
        var gap = $.global.ostrovProbeLast ? (now.getTime() - $.global.ostrovProbeLast) : 0;
        $.global.ostrovProbeLast = now.getTime();
        var parts = [stamp(now), "gap=" + gap + "ms"];
        try {
            var rq = app.project.renderQueue;
            parts.push("rendering=" + rq.rendering);
            parts.push("items=" + rq.numItems);
            for (var i = 1; i <= rq.numItems && i <= 5; i++) {
                var it = rq.item(i);
                var info = "#" + i + " " + statusName(it.status);
                try { info += " elapsed=" + it.elapsedSeconds; } catch (e1) { info += " elapsed=ERR"; }
                try { info += " comp=" + it.comp.name + " dur=" + it.timeSpanDuration + " fps=" + it.comp.frameRate; } catch (e2) { }
                try { info += " out=" + it.outputModule(1).file.fsName; } catch (e3) { info += " out=?"; }
                parts.push(info);
            }
            try { if (rq.lastError) parts.push("lastError=" + rq.lastError); } catch (e4) { }
        } catch (e) {
            parts.push("ERROR " + e.toString());
        }
        log(parts.join(" | "));
    };

    if ($.global.ostrovProbeTaskId) {
        try { app.cancelTask($.global.ostrovProbeTaskId); } catch (e) { }
    }

    var header = "=== probe v2 started " + new Date().toString() + " | AE " + app.version + " | engine=" + $.engineName;
    if (!log(header)) {
        alert("Не удалось записать лог в %AppData%\\Ostrov.\nВключите: Edit > Preferences > Scripting & Expressions >\n\"Allow Scripts to Write Files and Access Network\" и запустите скрипт снова.");
        return;
    }

    $.global.ostrovProbeLast = 0;
    $.global.ostrovProbeTick();
    $.global.ostrovProbeTaskId = app.scheduleTask("ostrovProbeTick()", 1000, true);
    // Окон (alert) здесь больше нет: они блокируют задачи. Запуск виден по строке "probe v2 started" в логе.
})();

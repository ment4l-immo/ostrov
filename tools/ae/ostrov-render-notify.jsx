// Остров: рендер After Effects на пассивном индикаторе (этап 5в). Рабочий скрипт.
//
// Раз в секунду, пока AE свободен, переписывает файл состояния очереди рендеринга:
//   %AppData%\Ostrov\ae-events\state.txt, строки вида  item=СТАТУС|секунды|композиция|путь_к_файлу
// Остров читает его и сам определяет рендер: на время рендера AE занят, скрипт молчит (проверено разведкой, AE 26.0),
// а когда рендер закончился, скрипт оживает и записывает DONE или ERR_STOPPED. Файл — это и есть «пульс» скрипта.
// Ни сети, ни портов: только файл.
//
// УСТАНОВКА (один раз):
//   1. В AE включить: Edit > Preferences > Scripting & Expressions > "Allow Scripts to Write Files and Access Network".
//   2. Скопировать этот файл в папку (Windows спросит права администратора):
//      C:\Program Files\Adobe\Adobe After Effects 2026\Support Files\Scripts\Startup
//   3. Перезапустить After Effects.
// Без установки в Startup скрипт можно запускать вручную: File > Scripts > Run Script File... (после каждого запуска AE).
//
// Окон (alert) здесь нет намеренно: они блокируют задачи скрипта.

#targetengine "ostrovRender"

(function () {
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
        return "UNKNOWN";
    }

    // В разделителе полей нельзя оставлять символы переноса строки и вертикальную черту.
    function clean(text) {
        return String(text).replace(/[\r\n|]+/g, " ");
    }

    function stateFile() {
        var root = new Folder(Folder.userData.fsName + "/Ostrov");
        if (!root.exists) root.create();
        var dir = new Folder(root.fsName + "/ae-events");
        if (!dir.exists) dir.create();
        return new File(dir.fsName + "/state.txt");
    }

    function writeState(text) {
        try {
            var f = stateFile();
            f.encoding = "UTF-8";
            if (f.open("w")) {
                f.write(text);
                f.close();
            }
        } catch (e) { }
    }

    $.global.ostrovRenderTick = function () {
        try {
            var rq = app.project.renderQueue;
            var lines = ["version=1", "rendering=" + rq.rendering];
            var count = rq.numItems;
            for (var i = 1; i <= count && i <= 40; i++) {
                var it = rq.item(i);
                var elapsed = 0;
                var comp = "";
                var out = "";
                try { elapsed = it.elapsedSeconds || 0; } catch (e1) { }
                try { comp = it.comp.name; } catch (e2) { }
                try { out = it.outputModule(1).file.fsName; } catch (e3) { }
                lines.push("item=" + statusName(it.status) + "|" + elapsed + "|" + clean(comp) + "|" + clean(out));
            }
            writeState(lines.join("\n") + "\n");
        } catch (e) { }
    };

    if ($.global.ostrovRenderTaskId) {
        try { app.cancelTask($.global.ostrovRenderTaskId); } catch (e) { }
    }
    $.global.ostrovRenderTick();
    $.global.ostrovRenderTaskId = app.scheduleTask("ostrovRenderTick()", 1000, true);
})();

window.todoUi = window.todoUi || {};

(() => {
  function parseDate(input) {
    if (!input) return null;
    const date = input instanceof Date ? input : new Date(input);
    if (Number.isNaN(date.getTime())) return null;
    return new Date(date.getFullYear(), date.getMonth(), date.getDate());
  }

  function startOfMonth(date) {
    return new Date(date.getFullYear(), date.getMonth(), 1);
  }

  function sameDay(a, b) {
    return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  }

  function formatMonthTitle(date) {
    return date.toLocaleDateString("de-DE", { month: "long", year: "numeric" });
  }

  function formatDateLabel(date) {
    return date.toLocaleDateString("de-DE", { day: "2-digit", month: "2-digit" });
  }

  function normalizeEvents(events) {
    return (events || [])
      .map((ev) => {
        const start = parseDate(ev.start ?? ev.Start);
        const endExclusive = parseDate(ev.end ?? ev.End);
        if (!start || !endExclusive) return null;

        // Backend liefert Ende exklusiv (+1 Tag). Für Anzeige inklusiv zurückrechnen.
        const endInclusive = new Date(endExclusive);
        endInclusive.setDate(endInclusive.getDate() - 1);

        return {
          taskId: ev.taskId ?? ev.TaskId ?? null,
          listId: ev.listId ?? ev.ListId ?? null,
          title: ev.title ?? ev.Title ?? "(Ohne Titel)",
          start,
          end: endInclusive >= start ? endInclusive : start,
          cardColor: normalizeHex(ev.cardColor ?? ev.CardColor),
          cardColorMode: ev.cardColorMode ?? ev.CardColorMode ?? 0,
          assigneeUserId: ev.assigneeUserId ?? ev.AssigneeUserId ?? null,
          assigneeDisplayName: ev.assigneeDisplayName ?? ev.AssigneeDisplayName ?? null,
          assigneeInitials: ev.assigneeInitials ?? ev.AssigneeInitials ?? null,
          assigneeAvatarColor: ev.assigneeAvatarColor ?? ev.AssigneeAvatarColor ?? null,
          assigneeProfilePictureUrl: ev.assigneeProfilePictureUrl ?? ev.AssigneeProfilePictureUrl ?? null
        };
      })
      .filter(Boolean);
  }

  function normalizeHex(value) {
    if (!value || typeof value !== "string") return null;
    let hex = value.trim();
    if (!hex) return null;
    if (!hex.startsWith("#")) hex = `#${hex}`;
    if (/^#[0-9a-fA-F]{3}$/.test(hex)) {
      hex = `#${hex[1]}${hex[1]}${hex[2]}${hex[2]}${hex[3]}${hex[3]}`;
    }
    return /^#[0-9a-fA-F]{6}$/.test(hex) ? hex.toLowerCase() : null;
  }

  function readableTextColor(hex) {
    const color = normalizeHex(hex);
    if (!color) return "#1e3a8a";
    const r = parseInt(color.slice(1, 3), 16);
    const g = parseInt(color.slice(3, 5), 16);
    const b = parseInt(color.slice(5, 7), 16);
    const luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
    return luminance > 0.62 ? "#0f172a" : "#ffffff";
  }

  function renderCalendar(element, state) {
    const { currentMonth, events, dotNetRef } = state;
    const monthStart = startOfMonth(currentMonth);
    const firstVisible = new Date(monthStart);
    firstVisible.setDate(monthStart.getDate() - ((monthStart.getDay() + 6) % 7));

    const today = new Date();
    const todayDate = new Date(today.getFullYear(), today.getMonth(), today.getDate());

    element.innerHTML = "";

    const wrapper = document.createElement("div");
    wrapper.className = "todo-calendar";

    const header = document.createElement("div");
    header.className = "todo-calendar__header";

    const controlsLeft = document.createElement("div");
    controlsLeft.className = "todo-calendar__controls";

    const prevBtn = document.createElement("button");
    prevBtn.type = "button";
    prevBtn.className = "todo-calendar__btn";
    prevBtn.textContent = "←";
    prevBtn.addEventListener("click", () => {
      state.currentMonth = new Date(currentMonth.getFullYear(), currentMonth.getMonth() - 1, 1);
      renderCalendar(element, state);
    });

    const nextBtn = document.createElement("button");
    nextBtn.type = "button";
    nextBtn.className = "todo-calendar__btn";
    nextBtn.textContent = "→";
    nextBtn.addEventListener("click", () => {
      state.currentMonth = new Date(currentMonth.getFullYear(), currentMonth.getMonth() + 1, 1);
      renderCalendar(element, state);
    });

    const todayBtn = document.createElement("button");
    todayBtn.type = "button";
    todayBtn.className = "todo-calendar__btn";
    todayBtn.textContent = "Heute";
    todayBtn.addEventListener("click", () => {
      state.currentMonth = new Date(todayDate.getFullYear(), todayDate.getMonth(), 1);
      renderCalendar(element, state);
    });

    controlsLeft.append(prevBtn, nextBtn, todayBtn);

    const title = document.createElement("h2");
    title.className = "todo-calendar__title";
    title.textContent = formatMonthTitle(currentMonth);

    header.append(controlsLeft, title);

    const weekdays = document.createElement("div");
    weekdays.className = "todo-calendar__weekdays";
    ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"].forEach((dayName) => {
      const cell = document.createElement("div");
      cell.textContent = dayName;
      weekdays.appendChild(cell);
    });

    const grid = document.createElement("div");
    grid.className = "todo-calendar__grid";

    for (let index = 0; index < 42; index += 1) {
      const day = new Date(firstVisible);
      day.setDate(firstVisible.getDate() + index);

      const cell = document.createElement("div");
      cell.className = "todo-calendar__day";
      if (day.getMonth() !== currentMonth.getMonth()) {
        cell.classList.add("is-outside");
      }
      if (sameDay(day, todayDate)) {
        cell.classList.add("is-today");
      }

      const dayNumber = document.createElement("div");
      dayNumber.className = "todo-calendar__day-number";
      dayNumber.textContent = String(day.getDate());
      cell.appendChild(dayNumber);

      const dayEvents = events.filter((ev) => day >= ev.start && day <= ev.end);
      dayEvents.slice(0, 3).forEach((ev) => {
        const eventEl = document.createElement("div");
        eventEl.className = "todo-calendar__event";
        eventEl.title = `${ev.title} (${formatDateLabel(ev.start)} - ${formatDateLabel(ev.end)})`;
        if (ev.cardColor) {
          if (Number(ev.cardColorMode) === 1) {
            eventEl.classList.add("is-full-color");
            eventEl.style.backgroundColor = ev.cardColor;
            eventEl.style.color = readableTextColor(ev.cardColor);
            eventEl.style.borderColor = ev.cardColor;
          } else {
            eventEl.style.setProperty("--todo-calendar-card-color", ev.cardColor);
          }
        }

        const titleText = document.createElement("span");
        titleText.className = "todo-calendar__event-title";
        titleText.textContent = ev.title;
        eventEl.appendChild(titleText);

        if (ev.assigneeUserId) {
          const avatar = document.createElement("span");
          avatar.className = "todo-calendar__avatar";
          avatar.title = ev.assigneeDisplayName || ev.assigneeUserId;

          const fallback = document.createElement("span");
          fallback.className = "todo-calendar__avatar-fallback";
          fallback.style.backgroundColor = ev.assigneeAvatarColor || "#64748b";
          fallback.textContent = ev.assigneeInitials || "?";
          avatar.appendChild(fallback);

          if (ev.assigneeProfilePictureUrl) {
            const image = document.createElement("img");
            image.src = ev.assigneeProfilePictureUrl;
            image.alt = "";
            image.loading = "lazy";
            image.addEventListener("load", () => {
              fallback.style.display = "none";
            });
            image.addEventListener("error", () => {
              image.style.display = "none";
            });
            avatar.appendChild(image);
          }

          eventEl.appendChild(avatar);
        }

        if (dotNetRef && ev.taskId) {
          eventEl.style.cursor = "pointer";
          eventEl.tabIndex = 0;
          eventEl.setAttribute("role", "button");
          eventEl.addEventListener("click", () => {
            dotNetRef.invokeMethodAsync("OnCalendarItemClick", String(ev.taskId));
          });
          eventEl.addEventListener("keydown", (event) => {
            if (event.key === "Enter" || event.key === " ") {
              event.preventDefault();
              dotNetRef.invokeMethodAsync("OnCalendarItemClick", String(ev.taskId));
            }
          });
        }
        cell.appendChild(eventEl);
      });

      if (dayEvents.length > 3) {
        const more = document.createElement("div");
        more.className = "todo-calendar__more";
        more.textContent = `+${dayEvents.length - 3} weitere`;
        cell.appendChild(more);
      }

      grid.appendChild(cell);
    }

    wrapper.append(header, weekdays, grid);
    element.appendChild(wrapper);
  }

  window.todoUi.initCalendar = (element, events, dotNetRef) => {
    if (!element) return;

    const normalizedEvents = normalizeEvents(events);
    const now = new Date();
    const currentMonth = new Date(now.getFullYear(), now.getMonth(), 1);

    const state = {
      currentMonth,
      events: normalizedEvents,
      dotNetRef: dotNetRef ?? null
    };

    element._calendarState = state;
    renderCalendar(element, state);
  };
})();

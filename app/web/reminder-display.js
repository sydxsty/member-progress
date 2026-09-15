/* Shared presentation decisions; reminders themselves keep their existing scope and schedule. */
(() => {
  'use strict';

  function rowAlarms(alarms) {
    return (alarms || []).filter(alarm => alarm && ['student', 'project'].includes(alarm.scopeType));
  }

  function isToday(when, now = new Date()) {
    if (when === null || when === undefined || when === '') return false;
    const date = when instanceof Date ? when : new Date(when);
    const current = now instanceof Date ? now : new Date(now);
    return Number.isFinite(+date) && Number.isFinite(+current) &&
      date.getFullYear() === current.getFullYear() &&
      date.getMonth() === current.getMonth() && date.getDate() === current.getDate();
  }

  function isTodayAlarm(alarm, now = new Date()) {
    return !!alarm && ((!!alarm.enabled && isToday(alarm.when, now)) || isToday(alarm.lastTriggeredWhen, now));
  }

  globalThis.ReminderDisplay = Object.freeze({ rowAlarms, isToday, isTodayAlarm });
})();

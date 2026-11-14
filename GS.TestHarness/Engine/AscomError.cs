/* Copyright(C) 2026  Rob Morgan (robert.morgan.e@gmail.com)

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published
    by the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System.Runtime.InteropServices;
using ASCOM;
using ASCOM.Common.Alpaca;

namespace GS.TestHarness.Engine;

/// <summary>The ASCOM error kinds a check can expect a member to raise.</summary>
public enum AscomErrorKind
{
    None,
    NotImplemented,
    InvalidValue,
    ValueNotSet,
    NotConnected,
    Parked,
    Slaved,
    InvalidOperation,
    ActionNotImplemented,
    OperationCancelled,
    Unspecified,
    DriverSpecific,

    /// <summary>Not an ASCOM error at all (a .NET exception, an HTTP failure, a COM error with no ASCOM code).</summary>
    Other,

    /// <summary>The call didn't answer in time.</summary>
    Timeout
}

/// <summary>Classifies whatever a member raised, whether it came over Alpaca or COM.</summary>
public static class AscomError
{
    // ASCOM errors over COM are HRESULTs 0x80040400-0x80040FFF (FACILITY_ITF). Kept as int so a negative HRESULT
    // isn't widened to long (and sign-extended) in the masking.
    private const int FacilityMask = unchecked((int)0xFFFFF000);
    private const int FacilityItf = unchecked((int)0x80040000);

    /// <summary>The ASCOM error kind of <paramref name="ex"/>, and its code if it has one.</summary>
    public static (AscomErrorKind Kind, int? Code) Classify(Exception? ex)
    {
        ex = Unwrap(ex);
        switch (ex)
        {
            case null: return (AscomErrorKind.None, null);
            case TimeoutException or TaskCanceledException: return (AscomErrorKind.Timeout, null);
            // Order matters: the specific ASCOM exceptions derive from DriverException (and some from each other).
            case ActionNotImplementedException: return (AscomErrorKind.ActionNotImplemented, (int)AlpacaErrors.ActionNotImplementedException);
            case PropertyNotImplementedException or MethodNotImplementedException or ASCOM.NotImplementedException:
                return (AscomErrorKind.NotImplemented, (int)AlpacaErrors.NotImplemented);
            case InvalidValueException: return (AscomErrorKind.InvalidValue, (int)AlpacaErrors.InvalidValue);
            case ValueNotSetException: return (AscomErrorKind.ValueNotSet, (int)AlpacaErrors.ValueNotSet);
            case NotConnectedException: return (AscomErrorKind.NotConnected, (int)AlpacaErrors.NotConnected);
            case ParkedException: return (AscomErrorKind.Parked, (int)AlpacaErrors.InvalidWhileParked);
            case SlavedException: return (AscomErrorKind.Slaved, (int)AlpacaErrors.InvalidWhileSlaved);
            case ASCOM.InvalidOperationException: return (AscomErrorKind.InvalidOperation, (int)AlpacaErrors.InvalidOperationException);
            case OperationCancelledException: return (AscomErrorKind.OperationCancelled, (int)AlpacaErrors.OperationCancelledException);
            case DriverException driver: return FromCode(driver.Number);
            case COMException com: return FromHResult(com.HResult);
            default: return ex.HResult is var hr && (hr & FacilityMask) == FacilityItf && (hr & 0xFFF) >= 0x400
                ? FromHResult(hr)
                : (AscomErrorKind.Other, null);
        }
    }

    /// <summary>The kind for an ASCOM error number (0x400-0xFFF, or the COM HRESULT 0x80040400-0x80040FFF).</summary>
    public static (AscomErrorKind Kind, int? Code) FromCode(int code)
    {
        if ((code & FacilityMask) == FacilityItf) code &= 0xFFF;
        var kind = code switch
        {
            0 => AscomErrorKind.None,
            0x400 => AscomErrorKind.NotImplemented,
            0x401 => AscomErrorKind.InvalidValue,
            0x402 => AscomErrorKind.ValueNotSet,
            0x407 => AscomErrorKind.NotConnected,
            0x408 => AscomErrorKind.Parked,
            0x409 => AscomErrorKind.Slaved,
            0x40B => AscomErrorKind.InvalidOperation,
            0x40C => AscomErrorKind.ActionNotImplemented,
            0x40E => AscomErrorKind.OperationCancelled,
            0x4FF => AscomErrorKind.Unspecified,
            >= 0x500 and <= 0xFFF => AscomErrorKind.DriverSpecific,
            _ => AscomErrorKind.Other
        };
        return (kind, code);
    }

    private static (AscomErrorKind Kind, int? Code) FromHResult(int hresult)
        => (hresult & FacilityMask) == FacilityItf ? FromCode(hresult & 0xFFF) : (AscomErrorKind.Other, null);

    /// <summary>The exception that matters: past the wrappers that reflection, tasks and COM interop add.</summary>
    public static Exception? Unwrap(Exception? ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } or System.Reflection.TargetInvocationException && ex.InnerException is not null)
            ex = ex.InnerException;
        return ex;
    }

    /// <summary>How an error is shown in a finding: its kind, code and message.</summary>
    public static string Describe(Exception? ex)
    {
        ex = Unwrap(ex);
        if (ex is null) return "no error";
        var (kind, code) = Classify(ex);
        var name = kind switch
        {
            AscomErrorKind.Other or AscomErrorKind.Timeout => ex.GetType().Name,
            _ => $"{kind}" + (code is { } c ? $" (0x{c:X3})" : "")
        };
        return $"{name}: {ex.Message}";
    }

    /// <summary>The usual name of the exception for a kind, as the ASCOM documentation gives it.</summary>
    public static string ExpectedName(AscomErrorKind kind, bool property = false) => kind switch
    {
        AscomErrorKind.NotImplemented => property
            ? "PropertyNotImplementedException (Alpaca error 0x400)"
            : "MethodNotImplementedException (Alpaca error 0x400)",
        AscomErrorKind.InvalidValue => "InvalidValueException (0x401)",
        AscomErrorKind.ValueNotSet => "ValueNotSetException (0x402)",
        AscomErrorKind.NotConnected => "NotConnectedException (0x407)",
        AscomErrorKind.Parked => "ParkedException (0x408)",
        AscomErrorKind.InvalidOperation => "InvalidOperationException (0x40B)",
        AscomErrorKind.None => "no error",
        _ => kind.ToString()
    };
}
